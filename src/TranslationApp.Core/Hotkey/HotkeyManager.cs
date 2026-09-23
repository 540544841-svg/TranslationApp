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
    private bool _suspended;

    public HotkeyManager()
    {
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
    }

    /// <summary>
    /// 注册（或更新）指定名称的热键。更新时先尝试新组合，成功后再释放旧组合；
    /// 注册失败（热键被其他程序占用）返回 false，旧热键保持可用，
    /// 调用方只需提示用户更换组合。
    /// </summary>
    public bool TryRegister(string name, HotkeyDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!definition.IsValidKey)
        {
            return false;
        }

        if (IsHeldByOtherName(name, definition))
        {
            return false;
        }

        int? oldId = _idByName.TryGetValue(name, out var existingId) ? existingId : null;
        if (oldId.HasValue && _registered.TryGetValue(oldId.Value, out var current) && current == definition)
        {
            return true; // 同名同组合是幂等更新；Windows 会拒绝为同一组合重复分配新 ID。
        }

        if (_suspended)
        {
            // 设置窗口打开时所有全局热键暂时注销。这里先用注册/立即注销探测组合是否可用，
            // 更新的是“待恢复”定义，避免录制 Alt+D 时被应用自己的旧热键抢先吞掉。
            var probeId = _nextId++;
            if (!RegisterHotKey(_hwnd, probeId, (uint)definition.Modifiers, (uint)definition.VirtualKey))
            {
                return false;
            }

            UnregisterHotKey(_hwnd, probeId);
            if (oldId.HasValue)
            {
                _registered.Remove(oldId.Value);
                _nameById.Remove(oldId.Value);
            }

            _idByName[name] = probeId;
            _nameById[probeId] = name;
            _registered[probeId] = definition;
            return true;
        }

        var newId = _nextId++;
        if (!RegisterHotKey(_hwnd, newId, (uint)definition.Modifiers, (uint)definition.VirtualKey))
        {
            return false; // 旧组合仍持有，不会因新组合被占用而失效
        }

        if (oldId.HasValue && !UnregisterHotKey(_hwnd, oldId.Value))
        {
            UnregisterHotKey(_hwnd, newId);
            return false;
        }

        if (oldId.HasValue)
        {
            _registered.Remove(oldId.Value);
            _nameById.Remove(oldId.Value);
        }
        _idByName[name] = newId;
        _nameById[newId] = name;
        _registered[newId] = definition;
        return true;
    }

    /// <summary>注销指定名称的热键（未注册时静默忽略）。</summary>
    /// <summary>探测组合是否可用；不会改变当前注册或待恢复定义。</summary>
    public bool CanRegister(string name, HotkeyDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!definition.IsValidKey || IsHeldByOtherName(name, definition))
        {
            return false;
        }

        var currentId = _idByName.TryGetValue(name, out var id) ? id : (int?)null;
        if (currentId.HasValue && _registered.TryGetValue(currentId.Value, out var current) && current == definition)
        {
            return true;
        }

        var probeId = _nextId++;
        if (!RegisterHotKey(_hwnd, probeId, (uint)definition.Modifiers, (uint)definition.VirtualKey))
        {
            return false;
        }

        UnregisterHotKey(_hwnd, probeId);
        return true;
    }

    private bool IsHeldByOtherName(string name, HotkeyDefinition definition) =>
        _registered.Any(pair =>
            pair.Value == definition &&
            _nameById.TryGetValue(pair.Key, out var owner) &&
            !string.Equals(owner, name, StringComparison.Ordinal));

    public void Unregister(string name)
    {
        if (_idByName.TryGetValue(name, out var id))
        {
            if (!_suspended)
            {
                UnregisterHotKey(_hwnd, id);
            }
            _registered.Remove(id);
            _nameById.Remove(id);
            _idByName.Remove(name);
        }
    }

    /// <summary>当前是否持有指定名称的热键。</summary>
    public bool IsRegistered(string name) =>
        _idByName.TryGetValue(name, out var id) && _registered.ContainsKey(id);

    /// <summary>
    /// 暂停全部全局热键，供设置/引导窗口录制按键时使用。保留当前定义与槽位映射，
    /// 期间仍可通过 TryRegister 校验并更新待恢复的热键。
    /// </summary>
    public void SuspendAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_suspended)
        {
            return;
        }

        foreach (var id in _registered.Keys.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
        }

        _suspended = true;
    }

    /// <summary>恢复设置窗口期间暂停的全部热键，返回恢复失败的槽位名称。</summary>
    public IReadOnlyList<string> ResumeAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_suspended)
        {
            return [];
        }

        _suspended = false;
        var failures = new List<string>();
        foreach (var (id, definition) in _registered.ToArray())
        {
            if (RegisterHotKey(_hwnd, id, (uint)definition.Modifiers, (uint)definition.VirtualKey))
            {
                continue;
            }

            if (_nameById.Remove(id, out var name))
            {
                _idByName.Remove(name);
                failures.Add(name);
            }
            _registered.Remove(id);
        }

        return failures;
    }

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
