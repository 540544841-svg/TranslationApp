using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using TranslationApp.Interop;

namespace TranslationApp.Services;

/// <summary>
/// 一次截屏的结果：BGR32 字节缓冲（内存顺序 B,G,R,A）+ 供遮罩铺满显示的 WPF 位图。
/// 缓冲保留 alpha 通道的原始值（GDI 截屏常为 0），因此显示用 <see cref="PixelFormats.Bgr32"/>、
/// 识别用 <c>BitmapAlphaMode.Ignore</c>，任何环节都不依赖 alpha（13.2.2 陷阱①②）。
/// </summary>
public sealed class ScreenFrame
{
    public ScreenFrame(byte[] bgra, int width, int height, BitmapSource display)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
        Display = display;
    }

    public byte[] Bgra { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>铺满遮罩窗口的位图（PixelFormats.Bgr32）。</summary>
    public BitmapSource Display { get; }
}

/// <summary>
/// GDI 截屏（13.2.3 步骤 3）：GetDC(NULL) + CreateCompatibleDC + CreateDIBSection(32bpp, biHeight 取负) + BitBlt。
/// 截屏必须在遮罩窗口显示之前完成，否则会把遮罩自身截进去（AC 8）。
/// </summary>
internal static class ScreenCapturer
{
    /// <summary>截取指定的物理像素矩形（虚拟桌面坐标系）；失败返回 null。</summary>
    public static ScreenFrame? Capture(int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screenDc = IntPtr.Zero;
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            screenDc = ScreenInterop.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                Log.Error("GetDC(NULL) 失败，无法截屏");
                return null;
            }

            memoryDc = ScreenInterop.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                Log.Error("CreateCompatibleDC 失败，无法截屏");
                return null;
            }

            var info = new ScreenInterop.BITMAPINFO
            {
                BmiHeader = new ScreenInterop.BITMAPINFOHEADER
                {
                    BiSize = Marshal.SizeOf<ScreenInterop.BITMAPINFOHEADER>(),
                    BiWidth = width,
                    // 负高度 = 自上而下（top-down），与 WPF 位图行序一致，省一次翻转
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0,
                },
            };

            bitmap = ScreenInterop.CreateDIBSection(memoryDc, ref info, ScreenInterop.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Log.Error("CreateDIBSection 失败，无法截屏");
                return null;
            }

            previous = ScreenInterop.SelectObject(memoryDc, bitmap);
            if (!ScreenInterop.BitBlt(
                    memoryDc, 0, 0, width, height,
                    screenDc, left, top,
                    ScreenInterop.SRCCOPY | ScreenInterop.CAPTUREBLT))
            {
                Log.Error("BitBlt 失败，无法截屏");
                return null;
            }

            var stride = width * 4;
            var buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, buffer.Length);

            // 显示位图固定用 Bgr32：整屏透明/全黑正是因为误用了 Bgra32（13.2.2 陷阱②）
            var display = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buffer, stride);
            display.Freeze();
            return new ScreenFrame(buffer, width, height, display);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截屏异常");
            return null;
        }
        finally
        {
            // 所有 GDI 句柄必须释放，避免长时间运行后句柄泄漏
            if (memoryDc != IntPtr.Zero && previous != IntPtr.Zero)
            {
                ScreenInterop.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                ScreenInterop.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                ScreenInterop.DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ScreenInterop.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
