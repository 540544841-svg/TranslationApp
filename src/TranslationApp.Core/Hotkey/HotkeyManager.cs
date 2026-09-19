using System.Runtime.InteropServices;

namespace TranslationApp.Core.Hotkey;

/// <summary>
/// 全局热键管理器（FR-001）：Core 层自建 message-only 窗口接收 WM_HOTKEY，
/// 不依赖 WPF；依赖宿主线程（UI 线程）的消息泵派发消息，因此
/// 构造/Dispose/事件回调都在创建线程上，App 层无需再做线程调度。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3); // message-only 窗口父句柄

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

    public sealed class HotkeyPressedEventArgs : EventArgs
    {
        public string Name { get; }

        public HotkeyPressedEventArgs(string name) => Name = name;
    }

    /// <summary>热键按下事件（在管理器创建线程上触发）。</summary>
    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _nameById = new();
    private readonly Dictionary<int, HotkeyDefinition> _registered = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager()
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
    }

    /// <summary>
    /// 注册（或更新）指定名称的热键。同名重注册时先注销旧键；
    /// 注册失败（热键被其他程序占用）返回 false，此时旧热键已失效，
    /// 调用方应尽快恢复原热键或提示用户。
    /// </summary>
    public bool TryRegister(string name, HotkeyDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!definition.IsValidKey)
        {
            return false;
        }

        if (!_idByName.TryGetValue(name, out var id))
        {
            id = _nextId++;
            _idByName[name] = id;
            _nameById[id] = name;
        }
        else
        {
            UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
        }

        if (!RegisterHotKey(_hwnd, id, (uint)definition.Modifiers, (uint)definition.VirtualKey))
        {
            return false;
        }

        _registered[id] = definition;
        return true;
    }

    /// <summary>注销指定名称的热键（未注册时静默忽略）。</summary>
    public void Unregister(string name)
    {
        if (_idByName.TryGetValue(name, out var id))
        {
            UnregisterHotKey(_hwnd, id);
            _registered.Remove(id);
        }
    }

    /// <summary>当前是否持有指定名称的热键。</summary>
    public bool IsRegistered(string name) =>
        _idByName.TryGetValue(name, out var id) && _registered.ContainsKey(id);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey && _nameById.TryGetValue(wParam.ToInt32(), out var name))
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(name));
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr CreateMessageWindow()
    {
        var className = "TranslationApp_Hotkey_" + Guid.NewGuid().ToString("N");
        var wndClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = _wndProc, // 字段保活，防止委托被 GC
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

        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registered.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
}
