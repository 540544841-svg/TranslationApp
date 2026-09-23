namespace TranslationApp.Core.Capture;

/// <summary>
/// OCR 原生引擎的**主进程侧客户端**（P0「OCR 原生引擎进程隔离」）：
/// 把 <see cref="IOcrEngine"/> 的调用翻译成命名管道上的请求/响应，真正的 ONNX 推理跑在同 EXE
/// 自启动的子进程里（见 <see cref="OcrWorkerProtocol"/> 的类头注释）。
///
/// 【故障语义】子进程崩溃 / 管道断裂 / 响应超时 → 本次识别抛异常，由 <see cref="OcrEngineRouter"/>
/// 按既有 14.9.2 降级策略统一处理（本次回退系统 OCR + 一次性提示 + 本进程内不再重试初始化）。
/// **任何情况下主进程都不因原生库崩溃而退出** —— 这是本项的唯一目的。
///
/// 【生命周期】通道懒建立（首次识别才起子进程，windows 默认路径零开销）；非常驻时空闲
/// <see cref="PaddleSessionPolicy.IdleReleaseDelay"/> 后丢弃通道（关闭管道即让子进程自行退出，
/// 内存完整回落）；<c>OcrPaddleResident</c> 开启则保持通道。任何一次失败都会丢弃通道，
/// 使下一次调用重建——但按既有降级策略，失败后同一进程内不会再走到这里。
/// </summary>
public sealed class OcrWorkerClient : IOcrEngine, IDisposable
{
    /// <summary>会话初始化预算（与进程内实现同一口径：进程启动 + ONNX 会话创建 ≤10s）。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 单次请求上限 = 初始化预算 + 单次识别预算（15s）。子进程内部仍独立执行 5s 的识破超时，
    /// 所以热路径的真实上限还是 5s，这个 15s 只兜住「冷启动 + 首次识别」这一条路径。
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<Stream>> _openChannel;
    private readonly Func<bool> _resident;
    private readonly TimeSpan _idleReleaseDelay;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerLock = new();
    private Stream? _channel;
    private DateTimeOffset? _lastUsedUtc;
    private Timer? _idleTimer;
    private bool _disposed;

    /// <summary>
    /// </summary>
    /// <param name="openChannel">建立一条到隔离进程的**已连接**双工流（内部可负责启动进程）；失败抛异常。</param>
    /// <param name="resident">读取 <c>OcrPaddleResident</c>（每次判定时读，设置即时生效）。</param>
    /// <param name="idleReleaseDelay">空闲丢弃通道的时长；null = <see cref="PaddleSessionPolicy.IdleReleaseDelay"/>。</param>
    /// <param name="requestTimeout">单次请求上限；null = <see cref="RequestTimeout"/>。</param>
    /// <param name="connectTimeout">通道建立上限；null = <see cref="ConnectTimeout"/>。</param>
    public OcrWorkerClient(
        Func<CancellationToken, Task<Stream>> openChannel,
        Func<bool> resident,
        TimeSpan? idleReleaseDelay = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? connectTimeout = null)
    {
        _openChannel = openChannel ?? throw new ArgumentNullException(nameof(openChannel));
        _resident = resident ?? throw new ArgumentNullException(nameof(resident));
        _idleReleaseDelay = idleReleaseDelay ?? PaddleSessionPolicy.IdleReleaseDelay;
        _requestTimeout = requestTimeout ?? RequestTimeout;
        _connectTimeout = connectTimeout ?? ConnectTimeout;
    }

    /// <summary>当前是否已建立到隔离进程的通道（诊断与测试用）。</summary>
    public bool IsChannelOpen => _channel is not null;

    /// <inheritdoc />
    public async Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
    {
        // 与进程内实现同一口径：输入非法是**中性结果**（null），不是故障，不触发降级
        if (bgra is null || !OcrWorkerProtocol.IsValidRecognizeRequest(width, height, bgra.LongLength))
        {
            return null;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        // 全程持锁：既串行化对隔离进程的并发请求（worker 是单通道顺序处理），
        // 也保证空闲丢弃不会与进行中的请求并发。
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var channel = await EnsureChannelAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(_requestTimeout);
            try
            {
                var header = new OcrWorkerMessage
                {
                    Kind = OcrWorkerProtocol.KindRecognize,
                    Width = width,
                    Height = height,
                    Pixels = bgra.LongLength,
                    LanguageTag = languageTag,
                };
                await OcrWorkerProtocol.WriteRequestAsync(channel, header, bgra, timeout.Token)
                    .ConfigureAwait(false);

                var response = await OcrWorkerProtocol
                    .ReadMessageAsync(channel, OcrWorkerProtocol.HeaderFrameLimit, timeout.Token)
                    .ConfigureAwait(false);

                if (!response.Ok)
                {
                    throw new OcrWorkerException(response.Message ?? "OCR 隔离进程返回失败");
                }

                if (!string.Equals(response.Kind, OcrWorkerProtocol.KindResult, StringComparison.Ordinal))
                {
                    throw new OcrWorkerException($"OCR 隔离进程返回了非预期的消息类型：{response.Kind}");
                }

                _lastUsedUtc = DateTimeOffset.UtcNow;
                EnsureIdleTimer();
                return response.NoResult ? null : OcrWorkerProtocol.ToRecognition(response);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await DropChannelAsync().ConfigureAwait(false);
                throw new TimeoutException($"OCR 隔离进程单次识别超过 {_requestTimeout.TotalSeconds:0}s");
            }
            catch (Exception)
            {
                // 通道断裂 / 帧非法 / 子进程崩溃：丢弃通道（下次调用重建），异常交给路由降级
                await DropChannelAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 空闲丢弃通道（纯判定在 <see cref="PaddleSessionPolicy"/>）。internal 以便单测用固定时间点直接驱动，
    /// 不依赖定时器时序。返回是否真的丢弃了通道。
    /// </summary>
    internal bool TryReleaseIdleChannel(DateTimeOffset nowUtc)
    {
        if (_channel is null || !PaddleSessionPolicy.ShouldRelease(_lastUsedUtc, nowUtc, _resident()))
        {
            return false;
        }

        var channel = _channel;
        _channel = null;
        _lastUsedUtc = null;
        _ = DisposeChannelQuietlyAsync(channel);
        return true;
    }

    private async Task<Stream> EnsureChannelAsync()
    {
        if (_channel is not null)
        {
            return _channel;
        }

        using var timeout = new CancellationTokenSource(_connectTimeout);
        Stream channel;
        try
        {
            channel = await _openChannel(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"OCR 隔离进程启动超时（{_connectTimeout.TotalSeconds:0}s）");
        }

        _channel = channel;
        _lastUsedUtc = DateTimeOffset.UtcNow;
        return channel;
    }

    private async Task DropChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        _lastUsedUtc = null;
        if (channel is not null)
        {
            await DisposeChannelQuietlyAsync(channel).ConfigureAwait(false);
        }
    }

    private static async Task DisposeChannelQuietlyAsync(Stream channel)
    {
        try
        {
            // 关闭管道 → 子进程读到 EOF → 自行退出（不依赖 kill，也就不会误杀别人的进程）
            await channel.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 收尾失败不影响调用方结论
        }
    }

    private void EnsureIdleTimer()
    {
        if (_resident() || _idleReleaseDelay <= TimeSpan.Zero)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            _idleTimer ??= new Timer(OnIdleTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _idleTimer.Change(_idleReleaseDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimer(object? state)
    {
        // 请求进行中（拿不到锁）就不抢：该次请求结束时会重新排期
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (TryReleaseIdleChannel(DateTimeOffset.UtcNow))
            {
                lock (_timerLock)
                {
                    _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch
        {
            // 定时器回调绝不冒泡
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_timerLock)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        _gate.Wait();
        try
        {
            var channel = _channel;
            _channel = null;
            _lastUsedUtc = null;
            if (channel is not null)
            {
                _ = DisposeChannelQuietlyAsync(channel);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
