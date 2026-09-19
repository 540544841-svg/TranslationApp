using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 阴影外框 + 工具条条带（chrome）的几何扩展（v1.2 修复批 ④ / ②）：
/// 不变式改写为「窗口物理尺寸 = 图像像素 × zoom + 2×framePhys + chromePhys」。
/// 关键性质：frame = 0 且 chrome = 面板高度时，*WithChrome 与既有 *WithPanel 函数**逐位一致**
/// （旧函数原样保留、旧测试不回归）；frame &gt; 0 时窗口左上 = 选区原点 − frame（图片仍对齐原选区），
/// 缩放锚点相对**图片区矩形**（扣除四周 frame 与底部 chrome）不漂移。
/// </summary>
public sealed class PinChromeLayoutTests
{
    private static readonly PhysicalRect Work = new(0, 0, 1920, 1032);

    private const int ImageWidth = 800;

    private const int ImageHeight = 400;

    /// <summary>四周阴影边距（物理，单侧），如 16 DIP × 1.5 屏缩放。</summary>
    private const int Frame = 24;

    private const int Panel = 120;

    // ---------------- 与 *WithPanel 组函数的一致性性质 ----------------

    [Fact]
    public void frame为0且chrome为面板高度时_与WithPanel组函数逐位一致()
    {
        Assert.Equal(
            PinLayout.PhysicalSizeWithPanel(ImageWidth, ImageHeight, 1.5, Panel),
            PinLayout.PhysicalSizeWithChrome(ImageWidth, ImageHeight, 1.5, 0, Panel));

        Assert.Equal(
            PinLayout.InitialRectWithPanel(100, 200, ImageWidth, ImageHeight, Work, 1.0, Panel),
            PinLayout.InitialRectWithChrome(100, 200, ImageWidth, ImageHeight, Work, 1.0, 0, Panel));

        var current = PinLayout.InitialRectWithPanel(100, 100, ImageWidth, ImageHeight, Work, 1.0, Panel);
        Assert.Equal(
            PinLayout.ZoomAtWithPanel(current, 0.3, 0.7, 2.0, ImageWidth, ImageHeight, Panel, Work),
            PinLayout.ZoomAtWithChrome(current, 0.3, 0.7, 2.0, ImageWidth, ImageHeight, 0, Panel, Work));

        Assert.Equal(PinLayout.WorkWithoutPanel(Work, Panel), PinLayout.WorkWithoutChrome(Work, 0, Panel));
    }

    // ---------------- 含 frame 与 chrome 的不变式 ----------------

    [Fact]
    public void 物理尺寸等于图像乘zoom加两倍frame加chrome()
    {
        var (width, height) = PinLayout.PhysicalSizeWithChrome(ImageWidth, ImageHeight, 2.0, Frame, Panel);
        Assert.Equal((ImageWidth * 2) + (2 * Frame), width);
        Assert.Equal((ImageHeight * 2) + (2 * Frame) + Panel, height);
    }

    [Fact]
    public void 初始矩形_窗口左上是选区原点减frame_图片仍对齐原选区()
    {
        var rect = PinLayout.InitialRectWithChrome(600, 400, ImageWidth, ImageHeight, Work, 1.0, Frame, Panel);
        Assert.Equal(600 - Frame, rect.Left);
        Assert.Equal(400 - Frame, rect.Top);
        Assert.Equal(ImageWidth + (2 * Frame), rect.Width);
        Assert.Equal(ImageHeight + (2 * Frame) + Panel, rect.Height);
    }

    // ---------------- 缩放锚点（相对图片区矩形） ----------------

    [Fact]
    public void 缩放锚点相对图片区矩形_缩放后该点不动()
    {
        const double fx = 0.25;
        const double fy = 0.75;
        var start = PinLayout.InitialRectWithChrome(400, 350, ImageWidth, ImageHeight, Work, 1.0, Frame, Panel);

        // 鼠标在图片区内 (fx, fy) 处对应的屏幕物理点
        var anchorX = start.Left + Frame + (fx * (start.Width - (2 * Frame)));
        var anchorY = start.Top + Frame + (fy * (start.Height - (2 * Frame) - Panel));

        var zoomed = PinLayout.ZoomAtWithChrome(start, fx, fy, 2.0, ImageWidth, ImageHeight, Frame, Panel, Work);
        var afterX = zoomed.Left + Frame + (fx * (zoomed.Width - (2 * Frame)));
        var afterY = zoomed.Top + Frame + (fy * (zoomed.Height - (2 * Frame) - Panel));

        Assert.Equal(anchorX, afterX, 1); // 允许 1 px 取整误差
        Assert.Equal(anchorY, afterY, 1);
        Assert.Equal((ImageWidth * 2) + (2 * Frame), zoomed.Width);
        Assert.Equal((ImageHeight * 2) + (2 * Frame) + Panel, zoomed.Height);
    }

    [Fact]
    public void 缩放往返回到原尺寸与原位置()
    {
        var start = PinLayout.InitialRectWithChrome(600, 250, ImageWidth, ImageHeight, Work, 1.0, Frame, Panel);
        var zoomed = PinLayout.ZoomAtWithChrome(start, 0.5, 0.5, 2.0, ImageWidth, ImageHeight, Frame, Panel, Work);
        var back = PinLayout.ZoomAtWithChrome(zoomed, 0.5, 0.5, 1.0, ImageWidth, ImageHeight, Frame, Panel, Work);

        Assert.Equal(start.Left, back.Left);
        Assert.Equal(start.Top, back.Top);
        Assert.Equal(start.Width, back.Width);
        Assert.Equal(start.Height, back.Height);
    }

    // ---------------- 工作区与缩放上限 ----------------

    [Fact]
    public void WorkWithoutChrome_为图片加chrome留出完整空间()
    {
        var work = PinLayout.WorkWithoutChrome(Work, Frame, Panel);
        Assert.Equal(Work.Left + Frame, work.Left);
        Assert.Equal(Work.Top + Frame, work.Top);
        Assert.Equal(Work.Width - (2 * Frame), work.Width);
        Assert.Equal(Work.Height - (2 * Frame) - Panel, work.Height);

        // 缩放上限据此收紧：最大倍数下「图片 + 阴影外框 + chrome」整体放得进原工作区
        var limits = PinLayout.ResolveLimits(0.25, 4.0, work, ImageWidth, ImageHeight);
        var (width, height) = PinLayout.PhysicalSizeWithChrome(ImageWidth, ImageHeight, limits.Max, Frame, Panel);
        Assert.True(width <= Work.Width, $"{width} > {Work.Width}");
        Assert.True(height <= Work.Height, $"{height} > {Work.Height}");
    }

    // ---------------- ContentScale 重载 ----------------

    [Fact]
    public void ContentScale_重载扣除两倍frame_frame为0时与三参数版一致()
    {
        var physicalWidth = PinLayout.PhysicalSizeWithChrome(ImageWidth, ImageHeight, 1.5, Frame, Panel).Width;
        var k = PinLayout.ContentScale(physicalWidth, 1.5, ImageWidth, Frame);
        Assert.Equal(1.0, k, 6); // 图片区 DIP 宽 = 图像像素宽（zoom 1.5 ÷ 屏缩放 1.5 = 1）

        Assert.Equal(
            PinLayout.ContentScale(physicalWidth, 1.5, ImageWidth),
            PinLayout.ContentScale(physicalWidth, 1.5, ImageWidth, 0), 10);
    }
}
