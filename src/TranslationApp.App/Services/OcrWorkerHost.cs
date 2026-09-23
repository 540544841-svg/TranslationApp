using System.IO.Pipes;
using System.IO;
using Serilog;
using TranslationApp.Core.Capture;

namespace TranslationApp.Services;

/// <summary>
/// OCR 隔离进程的**子进程侧**（由主进程用同一 EXE 以 <c>--ocr-worker &lt;管道名&gt;</c> 自启动）：
/// 建立单连接命名管道，顺序处理 <c>hello</c> / <c>recognize</c> / <c>shutdown</c> 请求，
/// 推理直接复用 <see cref="PaddleOcrEngine"/>（进程内那份实现与全部硬性配置原样保留）。
///
/// 【不做的事】不建托盘、不注册热键、不占单实例互斥体、不写用户数据、不开 UI。
/// 【退出条件】客户端断开管道（正常退出、主进程退出、被强杀）→ 读端 EOF → 进程自行退出。
/// 这也意味着**没有人需要 kill 子进程**：主进程一消失，管道句柄关闭，worker 自然收尾。
/// </summary>
internal static class OcrWorkerHost
{
    /// <summary>处理一次连接直到断开；返回即表示可以退出子进程。</summary>
    public static async Task RunAsync(string pipeName)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync().ConfigureAwait(false);
        Log.Information("OCR 隔离进程已连接主进程（管道 {Pipe}）", pipeName);

        // 会话常驻：子进程的生死由客户端决定（非常驻时空闲 5 分钟由客户端关闭管道），
        // 所以这里恒传 true，进程内那份空闲释放定时器在这条路径上不会触发。
        using var engine = new PaddleOcrEngine(() => true);

        while (true)
        {
            OcrWorkerMessage request;
            try
            {
                request = await OcrWorkerProtocol
                    .ReadMessageAsync(server, OcrWorkerProtocol.HeaderFrameLimit, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break; // 主进程正常收尾
            }
            catch (IOException ex)
            {
                Log.Debug(ex, "OCR 隔离进程管道中断");
                break;
            }
            catch (InvalidDataException ex)
            {
                Log.Warning(ex, "OCR 隔离进程收到非法帧，关闭通道");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            switch (request.Kind)
            {
                case OcrWorkerProtocol.KindShutdown:
                    await WriteQuietlyAsync(server, new OcrWorkerMessage { Kind = OcrWorkerProtocol.KindBye, Ok = true })
                        .ConfigureAwait(false);
                    return;

                case OcrWorkerProtocol.KindHello:
                    await WriteQuietlyAsync(server, new OcrWorkerMessage
                    {
                        Kind = OcrWorkerProtocol.KindHello,
                        Ok = true,
                        Pid = Environment.ProcessId,
                        Version = typeof(OcrWorkerHost).Assembly.GetName().Version?.ToString() ?? "unknown",
                    }).ConfigureAwait(false);
                    break;

                case OcrWorkerProtocol.KindRecognize:
                    if (!await HandleRecognizeAsync(server, engine, request).ConfigureAwait(false))
                    {
                        return; // 通道已断
                    }

                    break;

                default:
                    await WriteQuietlyAsync(server, OcrWorkerProtocol.Error($"未知消息类型：{request.Kind}"))
                        .ConfigureAwait(false);
                    break;
            }
        }

        Log.Information("OCR 隔离进程收到断开信号，退出");
    }

    /// <summary>处理一次识别请求；返回 false 表示通道已断，调用方应结束循环。</summary>
    private static async Task<bool> HandleRecognizeAsync(
        NamedPipeServerStream server, PaddleOcrEngine engine, OcrWorkerMessage request)
    {
        if (!OcrWorkerProtocol.IsValidRecognizeRequest(request.Width, request.Height, request.Pixels))
        {
            Log.Warning(
                "OCR 隔离进程拒绝非法识别请求：{Width}x{Height}，{Pixels} 字节",
                request.Width, request.Height, request.Pixels);
            await WriteQuietlyAsync(server, OcrWorkerProtocol.Error("识别请求的尺寸或像素长度非法")).ConfigureAwait(false);
            return true;
        }

        var pixels = new byte[request.Pixels];
        try
        {
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            Log.Debug(ex, "OCR 隔离进程读取像素载荷时通道断开");
            return false;
        }

        OcrRecognition? recognition;
        try
        {
            recognition = await engine.RecognizeAsync(pixels, request.Width, request.Height, request.LanguageTag)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 推理失败（含 5s 超时）不杀进程：把失败作为响应回给主进程，由既有降级策略处理
            Log.Warning(ex, "OCR 隔离进程推理失败");
            await WriteQuietlyAsync(server, OcrWorkerProtocol.Error(ex.Message)).ConfigureAwait(false);
            return true;
        }

        var response = recognition is null
            ? new OcrWorkerMessage { Kind = OcrWorkerProtocol.KindResult, Ok = true, NoResult = true }
            : OcrWorkerProtocol.FromRecognition(recognition);
        await WriteQuietlyAsync(server, response).ConfigureAwait(false);
        return true;
    }

    /// <summary>写响应；通道已断时静默返回 false（调用方据此结束循环/忽略）。</summary>
    private static async Task<bool> WriteQuietlyAsync(Stream stream, OcrWorkerMessage message)
    {
        try
        {
            await OcrWorkerProtocol.WriteMessageAsync(stream, message, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Log.Debug(ex, "OCR 隔离进程写响应失败（通道已断）");
            return false;
        }
    }
}
