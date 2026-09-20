using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 全局键盘观察钩子（FR-038 / spec §1）：WH_KEYBOARD_LL，**只观察不拦截**——
/// 与 <see cref="MouseButtonHook"/> 同款纪律：Start/Stop 幂等、Dispose 兜底、需在带消息泵的线程安装；
/// 回调里只做键码搬运，判定逻辑在 <see cref="ModifierKeyDoubleTapDetector"/>。
/// 隐私模式开启时绝不安装（B5 红线，门控在 App 层）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardButtonHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104; // Alt 组合按下的修饰路径，Win 键场景也走这里
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private readonly HookProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>任意键按下（vk, 是否本程序前台）。重复消息（auto-repeat）原样透传，由检测层忽略。</summary>
    public event Action<int, bool>? KeyDown;

    /// <summary>任意键抬起。</summary>
    public event Action<int, bool>? KeyUp;

    public KeyboardButtonHook() => _proc = Proc;

    public bool IsActive => _hook != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
        {
            return;
        }

        _hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                try
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var self = IsOwnProcessForeground();
                    var isDown = message is WmKeyDown or WmSysKeyDown;
                    // flags bit30 = 按下前键态：按下消息带该位 = auto-repeat，滤掉（抬起消息恒带该位，不适用）
                    var isRepeat = isDown && (data.Flags & 0x40000000) != 0;
                    if (isDown)
                    {
                        if (!isRepeat)
                        {
                            KeyDown?.Invoke((int)data.VirtualKey, self);
                        }
                    }
                    else
                    {
                        KeyUp?.Invoke((int)data.VirtualKey, self);
                    }
                }
                catch
                {
                    // 订阅方异常不得冒进系统输入链
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
