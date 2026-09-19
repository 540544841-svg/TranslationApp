using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 剪贴板监听（FR-017）：监听剪贴板变化，取出新复制的文本并对外抛出事件。
/// 处理策略（按需求）：
/// 1) 防抖：剪贴板变化后等待 300ms 再处理，避免一次复制触发多次；
/// 2) 限流：一次翻译触发后 3s 内不再触发；期间若有新复制，只保留最后一次，冷却结束后处理；
/// 3) 自写抑制：本程序写剪贴板（取词还原、复制译文）期间的变化必须忽略，否则会自我循环；
/// 4) Core 层自建 message-only 窗口接收 WM_CLIPBOARDUPDATE，不依赖 WPF。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private const int DebounceMs = 300;      // FR-017：复制后 300ms 防抖
    private const int CooldownMs = 3000;     // FR-017：3s 内多次复制只处理最后一次
    private static readonly IntPtr HwndMessage = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public WndProcDelegate? LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounceTimer;
    private readonly System.Threading.Timer _cooldownTimer;
    private string? _pendingText;
    private long _suppressUntilTicks;
    private DateTime _lastEmitUtc = DateTime.MinValue;
    private bool _listening;
    private bool _disposed;

    /// <summary>用户复制了文本（已过防抖与限流；在后台线程触发）。</summary>
    public event EventHandler<string>? TextCopied;

    public ClipboardMonitor()
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
        _debounceTimer = new System.Threading.Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        _cooldownTimer = new System.Threading.Timer(_ => OnCooldownElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>是否正在监听。</summary>
    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listening;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_listening || _disposed)
            {
                return;
            }

            _listening = AddClipboardFormatListener(_hwnd);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_listening)
            {
                return;
            }

            RemoveClipboardFormatListener(_hwnd);
            _listening = false;
            _pendingText = null;
        }
    }

    /// <summary>
    /// 在指定时长内忽略剪贴板变化（本程序自己写剪贴板时调用）。
    /// 取「更晚的到期时间」而非覆盖：两次抑制窗口重叠时，短窗口不得把长窗口缩短。
    /// </summary>
    public void Suppress(TimeSpan duration)
    {
        var until = DateTime.UtcNow.Add(duration).Ticks;
        var current = Interlocked.Read(ref _suppressUntilTicks);
        while (until > current)
        {
            var observed = Interlocked.CompareExchange(ref _suppressUntilTicks, until, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private bool IsSuppressed => DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
        {
            OnClipboardChanged();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnClipboardChanged()
    {
        if (IsSuppressed || !IsListening)
        {
            return;
        }

        // 防抖：每次变化都重置 300ms 计时，静默 300ms 后才真正处理
        _debounceTimer.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnDebounceElapsed()
    {
        if (IsSuppressed || !IsListening)
        {
            return;
        }

        var text = TryReadClipboardText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            var sinceLastEmit = DateTime.UtcNow - _lastEmitUtc;
            if (sinceLastEmit.TotalMilliseconds < CooldownMs)
            {
                // 限流窗口内：只记住最后一次，冷却结束后再处理
                _pendingText = text;
                _cooldownTimer.Change(
                    (int)(CooldownMs - sinceLastEmit.TotalMilliseconds), Timeout.Infinite);
                return;
            }

            _lastEmitUtc = DateTime.UtcNow;
        }

        TextCopied?.Invoke(this, text);
    }

    private void OnCooldownElapsed()
    {
        string? text;
        lock (_gate)
        {
            text = _pendingText;
            _pendingText = null;
        }

        if (string.IsNullOrEmpty(text) || IsSuppressed || !IsListening)
        {
            return;
        }

        lock (_gate)
        {
            _lastEmitUtc = DateTime.UtcNow;
        }

        TextCopied?.Invoke(this, text);
    }

    private static string? TryReadClipboardText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(20);
                continue;
            }

            try
            {
                if (!IsClipboardFormatAvailable(13)) // CF_UNICODETEXT
                {
                    return null;
                }

                var handle = GetClipboardData(13);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    private IntPtr CreateMessageWindow()
    {
        var className = "TranslationApp_Clipboard_" + Guid.NewGuid().ToString("N");
        var wndClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = _wndProc,
            HInstance = GetModuleHandleW(null),
            LpszClassName = className,
        };

        if (RegisterClassExW(ref wndClass) == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW 失败");
        }

        var hwnd = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, wndClass.HInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW 失败");
        }

        return hwnd;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _debounceTimer.Dispose();
        _cooldownTimer.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);
}
