namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 命名互斥体单实例守卫（FR-008/FR-012）。
/// 首次启动者持有互斥体并监听「激活」事件；重复启动者通过事件请求已有实例显形后立即退出，
/// 不弹阻塞式对话框（否则用户不点确定就会残留进程，且会挡住前台输入）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string ActivationSuffix = ".Activate";

    private readonly string _activationEventName;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activationEvent;
    private readonly CancellationTokenSource? _listenerCts;
    private readonly Thread? _listener;

    /// <summary>是否成功成为唯一实例。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>已有实例收到「用户又启动了一次」的请求（在后台线程触发，订阅方需自行切到 UI 线程）。</summary>
    public event EventHandler? ActivationRequested;

    public SingleInstanceGuard(string name)
    {
        _activationEventName = name + ActivationSuffix;
        _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        IsFirstInstance = createdNew;

        if (!createdNew)
        {
            SignalExistingInstance();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activationEventName);
        _listenerCts = new CancellationTokenSource();
        _listener = new Thread(ListenForActivation)
        {
            IsBackground = true, // 不阻止进程退出
            Name = "TranslationApp.ActivationListener",
        };
        _listener.Start();
    }

    /// <summary>通知已有实例显形（尽力而为：已有实例可能存在但尚未创建事件）。</summary>
    private void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(_activationEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 已有实例还未就绪，忽略
        }
        catch (UnauthorizedAccessException)
        {
            // 权限受限（如以不同用户运行），忽略
        }
    }

    private void ListenForActivation()
    {
        var cancellation = _listenerCts!.Token;
        using var stop = new ManualResetEventSlim(false);
        using var registration = cancellation.Register(stop.Set);

        while (!cancellation.IsCancellationRequested)
        {
            var signaled = WaitHandle.WaitAny([_activationEvent!, stop.WaitHandle], millisecondsTimeout: 500);
            if (signaled != 0)
            {
                continue;
            }

            try
            {
                ActivationRequested?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // 订阅方异常不影响监听线程
            }
        }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 当前线程不持有互斥体（异常场景），忽略
            }
        }

        _listenerCts?.Dispose();
        _activationEvent?.Dispose();
        _mutex.Dispose();
    }
}
