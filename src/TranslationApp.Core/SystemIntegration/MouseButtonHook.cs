using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 全局鼠标左键观察钩子（FR-036 / spec §2.1）：WH_MOUSE_LL，**只观察不拦截**——
/// 回调里仅拷贝坐标并触发事件，任何重活都交给订阅方（LL 钩子阻塞会拖慢全系统输入）。
/// 与 <see cref="ClipboardMonitor"/> 同款纪律：Start/Stop 幂等、Dispose 兜底、需在带消息泵的线程
/// （WPF UI 线程）上构造与 Start。隐私模式下绝不允许处于安装状态（门控在 App 层，spec §2.3）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MouseButtonHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmXButtonUp = 0x040C;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private readonly HookProc _proc; // 字段保活委托，防止被 GC 后回调跳飞
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>左键按下（物理坐标 + 前台窗口是否本进程）。在钩子所在线程触发。</summary>
    public event Action<int, int, bool>? LeftButtonDown;

    /// <summary>左键抬起（物理坐标 + Environment.TickCount64 + 前台归属）。在钩子所在线程触发。</summary>
    public event Action<int, int, long, bool>? LeftButtonUp;

    /// <summary>侧键抬起（FR-039）：button = 1（X1 后退）/ 2（X2 前进）。在钩子所在线程触发。</summary>
    public event Action<int, bool>? XButtonUp;

    public MouseButtonHook() => _proc = Proc;

    /// <summary>钩子当前是否已安装。</summary>
    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>安装钩子（幂等；安装失败只保持 IsActive=false，不抛异常）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
        {
            return;
        }

        _hook = SetWindowsHookExW(WhMouseLl, _proc, GetModuleHandleW(null), 0);
    }

    /// <summary>卸载钩子（幂等）——「一键全关」红线的落点。</summary>
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
            if (message is WmLButtonDown or WmLButtonUp or WmXButtonUp)
            {
                try
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var self = IsOwnProcessForeground();
                    if (message == WmLButtonDown)
                    {
                        LeftButtonDown?.Invoke(data.Pt.X, data.Pt.Y, self);
                    }
                    else if (message == WmLButtonUp)
                    {
                        LeftButtonUp?.Invoke(data.Pt.X, data.Pt.Y, Environment.TickCount64, self);
                    }
                    else
                    {
                        // mouseData 高 16 位 = XBUTTON1(1) / XBUTTON2(2)
                        var button = (int)(data.MouseData >> 16);
                        if (button is 1 or 2)
                        {
                            XButtonUp?.Invoke(button, self);
                        }
                    }
                }
                catch
                {
                    // 订阅方异常绝不能冒到系统输入路径（会拖死别人的鼠标）
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
