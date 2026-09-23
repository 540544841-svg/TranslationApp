using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranslationApp.Core.Capture;

/// <summary>
/// OCR 原生引擎进程隔离的线路协议（P0 唯一遗留项「OCR 原生引擎进程隔离」）。
///
/// 【为什么隔离】PaddleOCR 走 RapidOcrNet → ONNX Runtime 原生库，原生库一旦访问越界/拿到坏指针，
/// 崩溃的是**宿主进程**。速译是常驻托盘工具，一旦宿主挂掉，热键、剪贴板监听、托盘全部消失。
/// 隔离后原生代码跑在独立子进程：子进程崩 → 主进程只损失「本次识别」，回退系统 OCR 并提示（既有降级语义）。
///
/// 【为什么用同一个 EXE 自启动】子进程 = <c>TranslationApp.exe --ocr-worker &lt;管道名&gt;</c>。
/// 沿用同一可执行文件意味着：22.5MB 嵌入模型不重复打包、发布产物仍是单文件、
/// 无需新增工程与 CI 矩阵。WPF 的 <c>WinExe</c> 子系统还顺带保证子进程**没有控制台黑窗**。
///
/// 【帧格式】4 字节小端 Int32 长度 + UTF-8 JSON 正文。识别请求额外在头部帧之后紧跟
/// <c>pixels</c> 字节的 **原始 BGRA 缓冲**（不走 JSON/base64：4MP 图 16MB → base64 会膨胀到 21MB）。
///
/// 【坐标系】行框坐标属于**请求里那张位图**，与 <see cref="OcrRecognition"/> 语义完全一致，
/// 因此协议不引入任何新的坐标换算。<c>PaddleLayoutMapper</c> 只产出「单伪词行框」(词框 = 行框)，
/// 所以协议只传行级几何，客户端按同一不变式还原伪词框（见 <see cref="ToRecognition"/>）。
/// </summary>
public static class OcrWorkerProtocol
{
    /// <summary>子进程模式参数（主进程用同一 EXE 自启动 worker）。</summary>
    public const string WorkerArgument = "--ocr-worker";

    /// <summary>命名管道名前缀；每次连接用独立 GUID 后缀，避免多实例/重连串台。</summary>
    public const string PipeNamePrefix = "TranslationApp.OcrWorker.";

    /// <summary>头部/响应帧上限（1 MiB）。响应只含文本与行框，千行仍 &lt; 200KB。</summary>
    public const int HeaderFrameLimit = 1 << 20;

    /// <summary>原始像素载荷上限（100 MP × 4B = 400MB），与 <c>OcrEngine.MaxImageDimension</c>(10000) 同量级。</summary>
    public const long MaxPixelBytes = 100_000_000L * BgraImage.BytesPerPixel;

    /// <summary>握手请求（不建推理会话，仅确认隔离进程可启动可通信）。</summary>
    public const string KindHello = "hello";

    /// <summary>识别请求。</summary>
    public const string KindRecognize = "recognize";

    /// <summary>识别成功。</summary>
    public const string KindResult = "result";

    /// <summary>失败（推理异常/超时/请求非法）；<see cref="OcrWorkerMessage.Message"/> 带原因。</summary>
    public const string KindError = "error";

    /// <summary>请隔离进程正常退出。</summary>
    public const string KindShutdown = "shutdown";

    /// <summary>隔离进程确认退出。</summary>
    public const string KindBye = "bye";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 解析子进程参数：<c>--ocr-worker &lt;管道名&gt;</c>。是 worker 模式返回 true 并给出管道名，
    /// 否则 false（主进程照常走托盘启动）。放在 Core 以便单测覆盖参数边界。
    /// </summary>
    public static bool TryParseWorkerArguments(IReadOnlyList<string> args, out string pipeName)
    {
        pipeName = string.Empty;
        if (args is null)
        {
            return false;
        }

        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], WorkerArgument, StringComparison.Ordinal))
            {
                continue;
            }

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return false; // 参数不完整：按主进程处理，绝不半启动一个 worker
            }

            pipeName = args[i + 1];
            return true;
        }

        return false;
    }

    /// <summary>识别结果 → 响应消息（<c>Kind = result</c>）。</summary>
    public static OcrWorkerMessage FromRecognition(OcrRecognition recognition)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        return new OcrWorkerMessage
        {
            Kind = KindResult,
            Ok = true,
            Text = recognition.Text,
            Angle = recognition.TextAngle,
            EngineTag = recognition.EngineTag,
            Lines = recognition.Lines
                .Select(line => new OcrWorkerLine(
                    line.Index, line.Text, line.Rect.Left, line.Rect.Top, line.Rect.Right, line.Rect.Bottom))
                .ToArray(),
        };
    }

    /// <summary>
    /// 响应消息 → 识别结果。每个 <see cref="OcrWorkerLine"/> 还原成**单伪词行框**
    /// （词框 = 行框），与 <c>PaddleLayoutMapper.Map</c> 的输出逐字段同构。
    /// </summary>
    public static OcrRecognition ToRecognition(OcrWorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var lines = new List<OcrLineBox>(message.Lines?.Count ?? 0);
        foreach (var line in message.Lines ?? [])
        {
            var rect = new OcrRect(line.Left, line.Top, line.Right, line.Bottom);
            var text = line.Text ?? string.Empty;
            lines.Add(new OcrLineBox(line.Index, text, [new OcrWordBox(text, rect)], rect));
        }

        return new OcrRecognition(message.Text ?? string.Empty, lines, message.Angle, message.EngineTag);
    }

    /// <summary>构造一条失败消息。</summary>
    public static OcrWorkerMessage Error(string message) =>
        new() { Kind = KindError, Ok = false, Message = message };

    /// <summary>写入一个帧（长度前缀 + JSON 正文），并 flush。</summary>
    public static async Task WriteMessageAsync(Stream stream, OcrWorkerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>写入识别请求：先头部帧，再紧跟 <paramref name="pixels"/> 原始字节，最后统一 flush。</summary>
    public static async Task WriteRequestAsync(
        Stream stream, OcrWorkerMessage header, ReadOnlyMemory<byte> pixels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);
        var payload = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        if (!pixels.IsEmpty)
        {
            await stream.WriteAsync(pixels, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取一个帧。帧长度非法（负数/超上限）抛 <see cref="InvalidDataException"/>；
    /// 对端关闭抛 <see cref="EndOfStreamException"/>。两者都表示通道不可用，调用方据此收尾。
    /// </summary>
    public static async Task<OcrWorkerMessage> ReadMessageAsync(
        Stream stream, int frameLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > frameLimit)
        {
            throw new InvalidDataException($"OCR 隔离进程帧长度非法：{length}（上限 {frameLimit}）");
        }

        if (length == 0)
        {
            throw new InvalidDataException("OCR 隔离进程收到空帧");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        OcrWorkerMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<OcrWorkerMessage>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("OCR 隔离进程收到非 JSON 正文", ex);
        }

        return message ?? throw new InvalidDataException("OCR 隔离进程收到空 JSON 正文");
    }

    /// <summary>读取识别请求的像素载荷（调用方已校验长度）。</summary>
    public static async Task ReadPixelsAsync(
        Stream stream, byte[] destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(destination);
        await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>握手：发 <c>hello</c>、收应答。Doctor 用它验证隔离进程「起得来、说得上话」，不建推理会话。</summary>
    public static async Task<OcrWorkerMessage> HandshakeAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteMessageAsync(stream, new OcrWorkerMessage { Kind = KindHello }, cancellationToken)
            .ConfigureAwait(false);
        return await ReadMessageAsync(stream, HeaderFrameLimit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>请隔离进程退出（尽力而为；断开管道同样会让它自行退出）。</summary>
    public static async Task RequestShutdownAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteMessageAsync(stream, new OcrWorkerMessage { Kind = KindShutdown }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>校验识别请求的尺寸三元组是否自洽（宽高 &gt; 0、载荷长度覆盖 width×height×4 且不超上限）。</summary>
    public static bool IsValidRecognizeRequest(int width, int height, long pixels) =>
        width > 0
        && height > 0
        && pixels >= (long)width * height * BgraImage.BytesPerPixel
        && pixels <= MaxPixelBytes;

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 线路协议的单一信封（请求/响应共用）。刻意做成宽松的扁平结构：新增字段不破坏旧对端，
/// 反序列化也不需要按 kind 二次分派类型。
/// </summary>
public sealed record OcrWorkerMessage
{
    /// <summary>见 <see cref="OcrWorkerProtocol"/> 的 Kind* 常量。</summary>
    public string? Kind { get; init; }

    /// <summary>响应侧：true = 成功（<see cref="OcrWorkerProtocol.KindResult"/>/应答），false = 失败。</summary>
    public bool Ok { get; init; }

    /// <summary>识别请求：位图宽。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Width { get; init; }

    /// <summary>识别请求：位图高。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Height { get; init; }

    /// <summary>识别请求：紧随头部帧的原始像素字节数。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Pixels { get; init; }

    /// <summary>识别请求：识别语言标签（paddle 引擎忽略，仅记录）。</summary>
    public string? LanguageTag { get; init; }

    /// <summary>
    /// 识别响应：引擎明确返回「无结果」（输入非法等中性结果，非故障）。
    /// 与 <c>PaddleOcrEngine</c> 返回 null 同语义，客户端据此回传 null 而不触发降级。
    /// </summary>
    public bool NoResult { get; init; }

    /// <summary>识别响应：按行 \n 连接的文本。</summary>
    public string? Text { get; init; }

    /// <summary>识别响应：顺时针角度（正常横排是 −0 而非 null，判定必须看值）。</summary>
    public double? Angle { get; init; }

    /// <summary>识别响应：实际使用的识别语言标签（paddle 路径恒为 null）。</summary>
    public string? EngineTag { get; init; }

    /// <summary>识别响应：视觉行（含行框几何）。</summary>
    public IReadOnlyList<OcrWorkerLine>? Lines { get; init; }

    /// <summary>失败原因（<c>Kind = error</c>）。</summary>
    public string? Message { get; init; }

    /// <summary>握手应答：隔离进程 PID（诊断展示用）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pid { get; init; }

    /// <summary>握手应答：隔离进程版本号（诊断展示用）。</summary>
    public string? Version { get; init; }
}

/// <summary>一条视觉行的文本与行框（坐标属于请求位图；词框 = 行框的单伪词不变式见 <see cref="OcrWorkerProtocol.ToRecognition"/>）。</summary>
public sealed record OcrWorkerLine(int Index, string Text, double Left, double Top, double Right, double Bottom);

/// <summary>隔离进程侧识别失败（推理异常/超时/协议错误）时抛出的异常，供路由按既有降级策略处理。</summary>
public sealed class OcrWorkerException : Exception
{
    public OcrWorkerException(string message)
        : base(message)
    {
    }

    public OcrWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
