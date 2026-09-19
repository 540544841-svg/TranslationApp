using System.Runtime.InteropServices;

namespace TranslationApp.Interop;

/// <summary>
/// 截屏与遮罩窗口所需的 Win32 互操作（13.2.3）：
/// ① GDI 32bpp DIB 截屏（biHeight 取负 = 自上而下）；② SetWindowPos 以物理像素摆放窗口 + GetWindowRect 校验。
/// </summary>
internal static class ScreenInterop
{
    // BitBlt rop 码
    public const int SRCCOPY = 0x00CC0020;

    /// <summary>连同分层窗口一起拷贝（截图时不会漏掉带透明度的窗口内容）。</summary>
    public const int CAPTUREBLT = 0x40000000;

    public const int DIB_RGB_COLORS = 0;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int BiSize;
        public int BiWidth;
        public int BiHeight;
        public short BiPlanes;
        public short BiBitCount;
        public int BiCompression;
        public int BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public int BiClrUsed;
        public int BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER BmiHeader;
        public int BmiColors;
    }

    // ---------------- GDI 截屏 ----------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>32bpp DIB 段：直接把 pixels 暴露为指针，避免再经一次位图格式转换。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    // ---------------- 遮罩窗口物理像素摆放 ----------------

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    // ---------------- 钉图（FR-027）：目标显示器探测 ----------------

    /// <summary>
    /// 指定物理点所在显示器的工作区信息与缩放比（FR-027 钉图的定位与缩放上限判定）：
    /// 与 FR-025 使用同一套 API 顺序（MonitorFromPoint → GetMonitorInfoW → GetDpiForMonitor），
    /// 只是把「鼠标所在屏」换成「给定的物理点」。取不到缩放时按 96 DPI（1.0）兜底，绝不抛异常。
    /// </summary>
    public static bool TryGetMonitorAt(int x, int y, out NativeMethods.MONITORINFO info, out double scale)
    {
        info = new NativeMethods.MONITORINFO
        {
            CbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        scale = 1.0;

        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX != 0)
        {
            scale = dpiX / 96.0;
        }

        return true;
    }
}
