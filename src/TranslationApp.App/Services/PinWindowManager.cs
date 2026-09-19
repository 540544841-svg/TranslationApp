using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using TranslationApp.Core.Settings;
using TranslationApp.Windows;

namespace TranslationApp.Services;

/// <summary>
/// 钉图窗口的创建、登记、计数与上限判定（FR-027 / 14.3.10）：
/// 三重上限——张数 <c>PinMaxCount</c>(5)、单张 <c>PinMaxPixelsPerImage</c>(4 MP，**降采样后钉图并提示**)、
/// 总量 <c>PinMaxTotalPixels</c>(12 MP，**拒绝并提示**)；任一达到都绝不静默关闭已有钉图
/// （静默丢弃用户内容不可接受）。
///
/// 为什么上限盯得这么紧：需求 6 的「常驻（空闲）内存 &lt; 120 MB」是硬指标，位图是最容易击穿它的对象。
/// 因此本类只持有「当前活着的窗口」，关闭时随 <see cref="PinWindow.Closed"/> 立即摘除登记与像素计数，
/// 不放进任何静态/长生命周期缓存（14.3.10）。
/// </summary>
public sealed class PinWindowManager
{
    private readonly AppSettings _settings;
    private readonly Action<string, string> _balloon;
    private readonly List<PinWindow> _windows = [];

    public PinWindowManager(AppSettings settings, Action<string, string> balloon)
    {
        _settings = settings;
        _balloon = balloon;
    }

    /// <summary>钉图集合发生变化（新建/关闭）：托盘菜单据此决定「关闭所有钉图」是否可用。</summary>
    public event EventHandler? Changed;

    /// <summary>当前钉图张数。</summary>
    public int Count => _windows.Count;

    /// <summary>当前全部钉图的像素总量。</summary>
    public long TotalPixels { get; private set; }

    public bool HasPins => _windows.Count > 0;

    /// <summary>
    /// 把裁剪缓冲钉在屏幕上原选区的位置（<paramref name="monitorRect"/> 为选区所在显示器的物理矩形，
    /// <paramref name="imageRect"/> 为选区在其中的图像像素矩形）。返回是否成功；
    /// 失败原因（张数/像素总量超限）与降采样提示都以托盘气泡明确告知，**不静默失败**。
    /// <para>
    /// <paramref name="content"/> 是可选的译文与 OCR 布局：批 4c 传入真实数据即可原位显示译文
    /// （不传则用占位内容，切换机制与视觉过渡照常可用）。译文/识别内容**只进窗口、不进日志**（14.3.10）。
    /// </para>
    /// </summary>
    public bool TryPin(
        byte[] bgra, PixelRect imageRect, PhysicalRect monitorRect, PinContent? content = null,
        Func<Task<PinContent>>? retryAsync = null, Action? openInQuickWindow = null,
        Func<Task<PinContent>>? forceTranslateAsync = null)
    {
        if (bgra.Length == 0 || imageRect.IsEmpty)
        {
            Log.Warning("钉图被忽略：裁剪缓冲为空（{Length} 字节，选区 {Rect}）", bgra.Length, imageRect);
            return false;
        }

        var maxCount = _settings.PinMaxCount;
        var maxTotalPixels = (long)_settings.PinMaxTotalPixels;

        // 单张超限：按文档等比缩小到上限后钉图并提示（不拒绝用户）
        var (width, height, ratio, downscaled) =
            PinLayout.FitToPixelLimit(imageRect.Width, imageRect.Height, _settings.PinMaxPixelsPerImage);
        var pixels = (long)width * height;

        switch (PinLayout.CheckBudget(_windows.Count, TotalPixels, pixels, maxCount, maxTotalPixels))
        {
            case PinBudgetKind.CountExceeded:
                Log.Information("钉图被拒绝：张数已达上限 {Max}（当前 {Count}）", maxCount, _windows.Count);
                _balloon("速译 - 钉图", $"钉图数量已达上限（{maxCount} 张），请先关闭部分钉图后再试");
                return false;

            case PinBudgetKind.TotalExceeded:
                Log.Information(
                    "钉图被拒绝：像素总量将达到 {Total}（当前 {Current} + 新增 {New}），超过上限 {Max}",
                    TotalPixels + pixels, TotalPixels, pixels, maxTotalPixels);
                _balloon("速译 - 钉图", "钉图占用内存已达上限，请先关闭部分钉图后再试");
                return false;
        }

        try
        {
            if (downscaled)
            {
                bgra = BgraImage.Resize(bgra, imageRect.Width, imageRect.Height, width, height);
                // 口径统一（与气泡一致）：这里说的「X 倍缩小」恒为 ≥1 的缩小倍数（1/ratio），
                // 不是 ≤1 的缩放比值——否则日志里的 0.6 与气泡里的 1.6 看起来像两件事
                var shrink = 1 / ratio;
                Log.Information("钉图选区超过 {Max} px，已按 {Scale:0.###} 倍缩小（{From} → {To}）",
                    _settings.PinMaxPixelsPerImage, shrink,
                    $"{imageRect.Width}x{imageRect.Height}", $"{width}x{height}");
                _balloon("速译 - 钉图", $"选区过大，已按 {shrink:0.#} 倍缩小以节省内存");
            }

            // 裁剪缓冲的 alpha 不可信（GDI 截屏常为 0），故用 Bgr32 得到不透明图（与遮罩显示的同一处理）
            var stride = width * BgraImage.BytesPerPixel;
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgra, stride);
            source.Freeze(); // Freeze 后不可变、可跨线程、渲染更快；缓冲此后即可被 GC（14.3.10）

            var window = new PinWindow(
                source, width, height,
                monitorRect.Left + imageRect.X, monitorRect.Top + imageRect.Y, _settings, content,
                retryAsync, openInQuickWindow, forceTranslateAsync);
            window.Closed += OnWindowClosed;

            // **先登记后 Show**（14.3.10）：Show 抛异常时不会留下「已显示但未登记」的钉图
            // （那样的窗口 CloseAll 清不掉，用户只能逐个关，还会漏算像素总量）
            _windows.Add(window);
            TotalPixels += pixels;
            try
            {
                window.ShowPinned();
            }
            catch
            {
                // 显示失败即注销并关窗（关闭会释放位图），随后交给外层统一提示
                window.Closed -= OnWindowClosed;
                _windows.Remove(window);
                TotalPixels -= pixels;
                TryCloseAfterShowFailure(window);
                throw;
            }

            Changed?.Invoke(this, EventArgs.Empty);

            Log.Information(
                "钉图已创建：{Count}/{Max} 张，{Width}×{Height} px，合计 {Total} px，覆盖块 {Blocks} 个（物理 {Left},{Top}）",
                _windows.Count, maxCount, width, height, TotalPixels, content?.Blocks.Count ?? 0,
                window.PhysicalBounds.Left, window.PhysicalBounds.Top);
            return true;
        }
        catch (Exception ex)
        {
            // 创建/摆放失败绝不冒泡（需求 6 可靠性），只提示
            Log.Error(ex, "创建钉图窗口失败");
            _balloon("速译 - 钉图", "钉图失败，请重试（详见日志）");
            return false;
        }
    }

    /// <summary>
    /// Show 失败的收尾：注销后窗口已不在登记表里，CloseAll 再也清不掉它，因此这里必须自己关掉
    /// （Close() 会触发 <see cref="PinWindow"/> 的释放路径，把位图与覆盖层引用一并置空）。
    /// </summary>
    private static void TryCloseAfterShowFailure(PinWindow window)
    {
        try
        {
            window.ClosePin();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图显示失败后的关闭也失败，窗口已从登记表移除");
        }
    }

    /// <summary>托盘「关闭所有钉图」：逐个 Close()（每个窗口的 Closed 事件会摘除登记与像素计数）。</summary>
    public void CloseAll()
    {
        if (_windows.Count == 0)
        {
            return;
        }

        Log.Information("关闭所有钉图：{Count} 张", _windows.Count);
        foreach (var window in _windows.ToArray())
        {
            try
            {
                window.ClosePin();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "关闭钉图窗口失败");
            }
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not PinWindow window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        if (_windows.Remove(window))
        {
            TotalPixels -= window.Pixels;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Log.Information("钉图已关闭：剩余 {Count} 张，合计 {Total} px", _windows.Count, TotalPixels);
    }
}
