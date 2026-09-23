using System.IO.Pipes;
using System.Text;
using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// P0「OCR 原生引擎进程隔离」单测：线路协议（帧/像素载荷/换行、尺寸校验）与
/// <see cref="OcrWorkerClient"/> 的故障语义（结果映射、错误响应、通道断裂、空闲回收、非法输入）。
/// 用真实命名管道 + 内存里的「假隔离进程」跑端到端，只在 Windows 上运行（CI 即 windows-latest）。
/// </summary>
public sealed class OcrWorkerIsolationTests
{
    private static string NewPipeName() => "TranslationApp.Tests." + Guid.NewGuid().ToString("N");

    /// <summary>两个像素的 BGRA 缓冲（width×(height=?)：这里用 1×1×4 的最小合法缓冲）。</summary>
    private static byte[] Pixels(params byte[] values) => values;

    private static OcrRecognition SampleRecognition() => new(
        "hello world",
        [new OcrLineBox(0, "hello world", [new OcrWordBox("hello world", new OcrRect(10, 20, 110, 40))],
            new OcrRect(10, 20, 110, 40))],
        -0.0,
        EngineTag: null);

    // ==================== 假隔离进程 ====================

    /// <summary>
    /// 单连接假 worker：建管道 → 收请求 → 交给 <paramref name="handler"/> 回响应。
    /// 注意与服务端实例一一对应，断开后不能复用（与真实 worker 的「连接即生命周期」一致）。
    /// </summary>
    private sealed class FakeWorker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeWorker(Func<OcrWorkerMessage, Stream, CancellationToken, Task> handler)
        {
            PipeName = NewPipeName();
            Server = new NamedPipeServerStream(
                PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _loop = Task.Run(async () =>
            {
                try
                {
                    await Server.WaitForConnectionAsync(_cts.Token);
                    while (!_cts.IsCancellationRequested)
                    {
                        var request = await OcrWorkerProtocol
                            .ReadMessageAsync(Server, OcrWorkerProtocol.HeaderFrameLimit, _cts.Token);
                        await handler(request, Server, _cts.Token);
                    }
                }
                catch
                {
                    // 断开/取消即收尾，与真实 worker 的退出条件一致
                }
            });
        }

        public string PipeName { get; }

        public NamedPipeServerStream Server { get; }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await Server.DisposeAsync();
            }
            catch
            {
                // 忽略
            }

            try
            {
                await _loop;
            }
            catch
            {
                // 忽略
            }

            _cts.Dispose();
        }
    }

    /// <summary>每次 <see cref="OpenAsync"/> 都新起一个假 worker，并记录打开次数（验证懒连接/重建）。</summary>
    private sealed class WorkerFarm : IAsyncDisposable
    {
        private readonly Func<OcrWorkerMessage, Stream, CancellationToken, Task> _handler;
        private readonly List<FakeWorker> _workers = [];

        public WorkerFarm(Func<OcrWorkerMessage, Stream, CancellationToken, Task> handler) => _handler = handler;

        public int Opens { get; private set; }

        public async Task<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            Opens++;
            var worker = new FakeWorker(_handler);
            _workers.Add(worker);
            var client = new NamedPipeClientStream(".", worker.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(cancellationToken);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var worker in _workers)
            {
                await worker.DisposeAsync();
            }
        }
    }

    /// <summary>标准处理：读像素、回一条固定识别结果。应答的像素内容写入 <paramref name="received"/>。</summary>
    private static Func<OcrWorkerMessage, Stream, CancellationToken, Task> RespondWith(
        OcrRecognition recognition, Action<byte[]>? received = null) =>
        async (request, server, ct) =>
        {
            if (!string.Equals(request.Kind, OcrWorkerProtocol.KindRecognize, StringComparison.Ordinal))
            {
                await OcrWorkerProtocol.WriteMessageAsync(
                    server, new OcrWorkerMessage { Kind = request.Kind ?? "hello", Ok = true }, ct);
                return;
            }

            var pixels = new byte[request.Pixels];
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, ct);
            received?.Invoke(pixels);
            await OcrWorkerProtocol.WriteMessageAsync(server, OcrWorkerProtocol.FromRecognition(recognition), ct);
        };

    // ==================== OcrWorkerProtocol：帧与参数 ====================

    [Fact]
    public void TryParseWorkerArguments_识别子进程模式与管道名()
    {
        Assert.True(OcrWorkerProtocol.TryParseWorkerArguments(["--ocr-worker", "pipe-1"], out var name));
        Assert.Equal("pipe-1", name);

        // 参数不完整：按主进程处理，绝不半启动一个 worker
        Assert.False(OcrWorkerProtocol.TryParseWorkerArguments(["--ocr-worker"], out _));
        Assert.False(OcrWorkerProtocol.TryParseWorkerArguments(["--ocr-worker", "  "], out _));

        // 普通启动（含 --verbose/--settings）不受影响
        Assert.False(OcrWorkerProtocol.TryParseWorkerArguments(["--verbose", "--settings"], out _));
        Assert.False(OcrWorkerProtocol.TryParseWorkerArguments([], out _));
    }

    [Fact]
    public async Task 协议_请求头与原始像素分开成帧_逐字节还原()
    {
        var pixels = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var header = new OcrWorkerMessage
        {
            Kind = OcrWorkerProtocol.KindRecognize,
            Width = 4,
            Height = 4,
            Pixels = pixels.Length,
            LanguageTag = "auto",
        };

        using var stream = new MemoryStream();
        await OcrWorkerProtocol.WriteRequestAsync(stream, header, pixels, CancellationToken.None);
        stream.Position = 0;

        var read = await OcrWorkerProtocol.ReadMessageAsync(stream, OcrWorkerProtocol.HeaderFrameLimit, CancellationToken.None);
        Assert.Equal(OcrWorkerProtocol.KindRecognize, read.Kind);
        Assert.Equal(4, read.Width);
        Assert.Equal(4, read.Height);
        Assert.Equal(64, read.Pixels);
        Assert.Equal("auto", read.LanguageTag);

        var buffer = new byte[read.Pixels];
        await OcrWorkerProtocol.ReadPixelsAsync(stream, buffer, CancellationToken.None);
        Assert.Equal(pixels, buffer);
        Assert.Equal(stream.Length, stream.Position); // 无多余字节
    }

    [Fact]
    public async Task 协议_识别结果往返_行框还原为单伪词行框()
    {
        using var stream = new MemoryStream();
        await OcrWorkerProtocol.WriteMessageAsync(stream, OcrWorkerProtocol.FromRecognition(SampleRecognition()), CancellationToken.None);
        stream.Position = 0;

        var message = await OcrWorkerProtocol.ReadMessageAsync(stream, OcrWorkerProtocol.HeaderFrameLimit, CancellationToken.None);
        Assert.True(message.Ok);
        Assert.Equal(OcrWorkerProtocol.KindResult, message.Kind);

        var recognition = OcrWorkerProtocol.ToRecognition(message);
        Assert.Equal("hello world", recognition.Text);
        Assert.Equal(0.0, recognition.TextAngle!.Value);
        Assert.Null(recognition.EngineTag); // paddle 路径恒为 null（交回翻译引擎自动检测）
        var line = Assert.Single(recognition.Lines);
        Assert.Equal((10, 20, 110, 40), (line.Rect.Left, line.Rect.Top, line.Rect.Right, line.Rect.Bottom));
        var word = Assert.Single(line.Words); // 单伪词：词框 = 行框（与 PaddleLayoutMapper 同构）
        Assert.Equal(line.Rect, word.Rect);
        Assert.Equal(line.Text, word.Text);
    }

    [Fact]
    public async Task 协议_帧长度超上限_按非法帧拒绝()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }); // int32 小端 = int.MaxValue
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OcrWorkerProtocol.ReadMessageAsync(stream, OcrWorkerProtocol.HeaderFrameLimit, CancellationToken.None));
    }

    [Fact]
    public async Task 协议_对端关闭_抛EndOfStreamException而不是挂起()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            OcrWorkerProtocol.ReadMessageAsync(stream, OcrWorkerProtocol.HeaderFrameLimit, CancellationToken.None));
    }

    [Fact]
    public void 协议_尺寸校验_宽高非正或载荷不足一律非法()
    {
        Assert.True(OcrWorkerProtocol.IsValidRecognizeRequest(4, 4, 64));
        Assert.True(OcrWorkerProtocol.IsValidRecognizeRequest(4, 4, 128)); // 允许尾部余量（与 PaddleOcrEngine 同口径）
        Assert.False(OcrWorkerProtocol.IsValidRecognizeRequest(0, 4, 64));
        Assert.False(OcrWorkerProtocol.IsValidRecognizeRequest(4, -1, 64));
        Assert.False(OcrWorkerProtocol.IsValidRecognizeRequest(4, 4, 63)); // 差一字节
        Assert.False(OcrWorkerProtocol.IsValidRecognizeRequest(4, 4, OcrWorkerProtocol.MaxPixelBytes + 1));
    }

    // ==================== OcrWorkerClient：端到端 ====================

    [Fact]
    public async Task 客户端_正常识别_像素原样送达并还原结果()
    {
        byte[]? seen = null;
        await using var farm = new WorkerFarm(RespondWith(SampleRecognition(), pixels => seen = pixels));
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false);
        var pixels = Pixels(1, 2, 3, 4);

        var result = await client.RecognizeAsync(pixels, 1, 1, "auto");

        Assert.NotNull(result);
        Assert.Equal("hello world", result!.Text);
        Assert.Equal(110.0, Assert.Single(result.Lines).Rect.Right);
        Assert.Equal(pixels, seen);
        Assert.True(client.IsChannelOpen);
        Assert.Equal(1, farm.Opens); // 懒连接：一次识别只起一个子进程
    }

    [Fact]
    public async Task 客户端_连续识别_复用同一通道()
    {
        var opens = 0;
        await using var farm = new WorkerFarm(async (request, server, ct) =>
        {
            var pixels = new byte[request.Pixels];
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, ct);
            await OcrWorkerProtocol.WriteMessageAsync(server, OcrWorkerProtocol.FromRecognition(SampleRecognition()), ct);
        });
        using var client = new OcrWorkerClient(async ct =>
        {
            opens++;
            return await farm.OpenAsync(ct);
        }, () => true);

        await client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto");
        await client.RecognizeAsync(Pixels(5, 6, 7, 8), 1, 1, "auto");

        Assert.Equal(1, opens); // 同一进程内串行复用（不每次重建，冷启动只在首次付）
    }

    [Fact]
    public async Task 客户端_隔离进程返回错误响应_抛OcrWorkerException()
    {
        await using var farm = new WorkerFarm(async (request, server, ct) =>
        {
            var pixels = new byte[request.Pixels];
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, ct);
            await OcrWorkerProtocol.WriteMessageAsync(server, OcrWorkerProtocol.Error("模拟推理失败"), ct);
        });
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false);

        var ex = await Assert.ThrowsAsync<OcrWorkerException>(() =>
            client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto"));
        Assert.Contains("模拟推理失败", ex.Message, StringComparison.Ordinal);
        Assert.False(client.IsChannelOpen); // 失败后丢弃通道，异常交给路由降级
    }

    [Fact]
    public async Task 客户端_隔离进程崩溃_抛异常而非挂起_且不影响调用方进程()
    {
        await using var farm = new WorkerFarm(async (request, server, ct) =>
        {
            // 读到请求头之后立刻「崩掉」：关闭管道，不回任何响应
            await server.DisposeAsync();
        });
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false);

        // 通道断裂的两种表现（写失败 / 读 EOF）都属于「本次识别失败」，绝不静默成功也绝不永久挂起
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto"));
        Assert.False(client.IsChannelOpen);
    }

    [Fact]
    public async Task 客户端_输入非法_返回null且不建立通道()
    {
        await using var farm = new WorkerFarm(RespondWith(SampleRecognition()));
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false);

        Assert.Null(await client.RecognizeAsync([], 0, 10, "auto"));         // 宽非法
        Assert.Null(await client.RecognizeAsync([], 10, 10, "auto"));        // 缓冲为空
        Assert.Null(await client.RecognizeAsync([1, 2, 3], 10, 10, "auto")); // 缓冲不足

        Assert.Equal(0, farm.Opens); // 输入非法是中性结果，不是故障：连子进程都不起
    }

    [Fact]
    public async Task 客户端_引擎返回无结果_回传null不触发故障()
    {
        await using var farm = new WorkerFarm(async (request, server, ct) =>
        {
            var pixels = new byte[request.Pixels];
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, ct);
            await OcrWorkerProtocol.WriteMessageAsync(
                server, new OcrWorkerMessage { Kind = OcrWorkerProtocol.KindResult, Ok = true, NoResult = true }, ct);
        });
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false);

        Assert.Null(await client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto"));
        Assert.True(client.IsChannelOpen); // 中性结果保留通道
    }

    [Fact]
    public async Task 客户端_空闲到期丢弃通道_下次识别重建子进程()
    {
        await using var farm = new WorkerFarm(RespondWith(SampleRecognition()));
        // 空闲时长给足 1 小时，测试内定时器不会触发；用固定时间点直接驱动纯判定，避免时序抖动
        using var client = new OcrWorkerClient(farm.OpenAsync, () => false, idleReleaseDelay: TimeSpan.FromHours(1));

        await client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto");
        Assert.True(client.IsChannelOpen);

        Assert.False(client.TryReleaseIdleChannel(DateTimeOffset.UtcNow.AddMinutes(1))); // 未到期 → 保留
        Assert.True(client.TryReleaseIdleChannel(DateTimeOffset.UtcNow.AddHours(2)));    // 到期 → 丢弃
        Assert.False(client.IsChannelOpen);

        await client.RecognizeAsync(Pixels(5, 6, 7, 8), 1, 1, "auto");
        Assert.Equal(2, farm.Opens); // 重建：内存完整回落（AC 6）后下次识别重付冷启动
    }

    [Fact]
    public async Task 客户端_常驻开关_空闲永不丢弃通道()
    {
        await using var farm = new WorkerFarm(RespondWith(SampleRecognition()));
        using var client = new OcrWorkerClient(farm.OpenAsync, () => true, idleReleaseDelay: TimeSpan.FromHours(1));

        await client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto");

        Assert.False(client.TryReleaseIdleChannel(DateTimeOffset.UtcNow.AddDays(1))); // OcrPaddleResident=true
        Assert.True(client.IsChannelOpen);
    }

    [Fact]
    public async Task 客户端_启动失败_抛异常且下次调用重试()
    {
        var opens = 0;
        using var client = new OcrWorkerClient(_ =>
        {
            opens++;
            throw new InvalidOperationException("模拟隔离进程启动失败");
        }, () => false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto"));

        Assert.Equal(2, opens); // 失败不留半成品通道，每次调用都可重试
        Assert.False(client.IsChannelOpen);
    }

    // ==================== 与路由的协作（既有降级语义不变） ====================

    [Fact]
    public async Task 路由_隔离进程失败时_本次回退windows并一次性提示()
    {
        var windowsCalls = 0;
        var windows = new StubEngine(_ =>
        {
            windowsCalls++;
            return Task.FromResult<OcrRecognition?>(new OcrRecognition("from-windows", [], null, "en-US"));
        });
        var notices = new List<string>();
        var failures = new List<Exception>();

        await using var farm = new WorkerFarm(async (request, server, ct) =>
        {
            var pixels = new byte[request.Pixels];
            await OcrWorkerProtocol.ReadPixelsAsync(server, pixels, ct);
            await server.DisposeAsync(); // 模拟原生库崩溃
        });
        var client = new OcrWorkerClient(farm.OpenAsync, () => false);
        var router = new OcrEngineRouter(
            windows, client, () => OcrEngineNames.Paddle,
            notifyFallback: notices.Add, logPaddleFailure: failures.Add);

        var result = await router.RecognizeAsync(Pixels(1, 2, 3, 4), 1, 1, "auto");

        Assert.Equal("from-windows", result!.Text); // 本次识别仍拿到结果（降级到系统 OCR）
        Assert.Equal(1, windowsCalls);
        Assert.Equal(OcrEngineRouter.PaddleFallbackNotice, Assert.Single(notices));
        Assert.Single(failures);
        Assert.True(router.IsPaddleDegraded);
        client.Dispose();
    }

    private sealed class StubEngine(Func<(byte[] Bgra, int Width, int Height, string? LanguageTag), Task<OcrRecognition?>> handler)
        : IOcrEngine
    {
        public Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag) =>
            handler((bgra, width, height, languageTag));
    }
}
