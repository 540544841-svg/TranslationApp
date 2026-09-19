using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 模式 C（下方译文面板）的几何扩展与**坐标映射**（FR-027 / 14.3.5 / 14.3.7）：
/// 面板高度为 0 时所有函数必须与不带面板的版本逐位等价（批 4a/4b 行为不回归）；
/// 面板存在时不变式扩展为「窗口物理尺寸 = 图像像素 × zoom + 面板物理高度」。
/// 同时固定覆盖层的唯一换算系数 <c>k</c>：k = 窗口 DIP 宽 / 图像像素宽 = zoom / 屏缩放，
/// 缩放比 1.0 / 1.5 / 2.0 与 125%/150% 屏都要能往返。
/// </summary>
public sealed class PinPanelLayoutTests
{
    private static readonly PhysicalRect Work = new(0, 0, 1920, 1032);

    private const int ImageWidth = 800;

    private const int ImageHeight = 400;

    [Fact]
    public void 面板高度为_0_时_尺寸与初始矩形与无面板版本完全一致()
    {
        Assert.Equal(
            PinLayout.PhysicalSize(ImageWidth, ImageHeight, 1.5),
            PinLayout.PhysicalSizeWithPanel(ImageWidth, ImageHeight, 1.5, 0));

        Assert.Equal(
            PinLayout.InitialRect(100, 200, ImageWidth, ImageHeight, Work, 1.0),
            PinLayout.InitialRectWithPanel(100, 200, ImageWidth, ImageHeight, Work, 1.0, 0));

        Assert.Equal(
            PinLayout.ZoomAt(new PhysicalRect(100, 200, 800, 400), 0.5, 0.5, 2.0, ImageWidth, ImageHeight, Work),
            PinLayout.ZoomAtWithPanel(new PhysicalRect(100, 200, 800, 400), 0.5, 0.5, 2.0, ImageWidth, ImageHeight, 0, Work));

        Assert.Equal(Work, PinLayout.WorkWithoutPanel(Work, 0));
    }

    [Fact]
    public void 面板高度计入窗口物理高度()
    {
        var (width, height) = PinLayout.PhysicalSizeWithPanel(ImageWidth, ImageHeight, 1.0, 180);
        Assert.Equal(800, width);
        Assert.Equal(580, height);
    }

    [Fact]
    public void 面板先占工作区_缩放上限据此收紧_保证图片加面板整体可见()
    {
        var work = new PhysicalRect(0, 0, 1000, 500);
        var withoutPanel = PinLayout.ResolveLimits(0.25, 4.0, work, 400, 300);
        var withPanel = PinLayout.ResolveLimits(0.25, 4.0, PinLayout.WorkWithoutPanel(work, 100), 400, 300);

        Assert.True(withPanel.Max < withoutPanel.Max);
        Assert.True(withPanel.Min <= withPanel.Max);
        Assert.Equal(500 - 100, PinLayout.WorkWithoutPanel(work, 100).Height);
    }

    [Fact]
    public void 面板存在时鼠标锚点仍然不漂移_且锚点分数相对图片区域()
    {
        var panel = 160;
        var current = PinLayout.InitialRectWithPanel(100, 100, ImageWidth, ImageHeight, Work, 1.0, panel);
        Assert.Equal(ImageHeight + panel, current.Height);

        // 鼠标在图片区域内 1/4 高度处（锚点分数相对图片区域）
        var fractionY = 0.25;
        var anchorY = current.Top + (fractionY * (current.Height - panel));

        var zoomed = PinLayout.ZoomAtWithPanel(current, 0.5, fractionY, 2.0, ImageWidth, ImageHeight, panel, Work);
        var afterAnchor = zoomed.Top + (fractionY * (zoomed.Height - panel));

        Assert.Equal(anchorY, afterAnchor, 1); // 允许 1 px 取整误差
        Assert.Equal((ImageHeight * 2) + panel, zoomed.Height);
    }

    [Fact]
    public void 面板存在时缩放往返回到原尺寸()
    {
        var panel = 120;
        var start = PinLayout.InitialRectWithPanel(0, 0, ImageWidth, ImageHeight, Work, 1.0, panel);
        var zoomed = PinLayout.ZoomAtWithPanel(start, 0.5, 0.5, 2.0, ImageWidth, ImageHeight, panel, Work);
        var back = PinLayout.ZoomAtWithPanel(zoomed, 0.5, 0.5, 1.0, ImageWidth, ImageHeight, panel, Work);

        Assert.Equal(start.Width, back.Width);
        Assert.Equal(start.Height, back.Height);
    }

    // ---------------- 坐标映射：k 与缩放比 ----------------

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 1.25)]
    [InlineData(1.5, 1.5)]
    [InlineData(2.0, 2.0)]
    public void 换算系数_k_等于_zoom_除屏缩放_且图像像素到_DIP_往返无损(double zoom, double dpiScale)
    {
        var (physicalWidth, _) = PinLayout.PhysicalSize(ImageWidth, ImageHeight, zoom);
        var k = PinLayout.ContentScale(physicalWidth, dpiScale, ImageWidth);

        Assert.Equal(zoom / dpiScale, k, 6);

        // 图像整幅宽度换算到 DIP 后必须等于窗口的 DIP 宽度
        Assert.Equal(PinLayout.DipLength(physicalWidth, dpiScale), k * ImageWidth, 3);

        // 覆盖块（图像像素）→ DIP → 再换回图像像素，误差 < 0.01 px
        foreach (var imageX in new[] { 0.0, 12.5, 137.0, ImageWidth - 1.0 })
        {
            var dipX = imageX * k;
            Assert.Equal(imageX, dipX / k, 6);
        }
    }

    [Fact]
    public void 缩放倍数变化时_同一图像像素的屏幕位置按_k_线性变化()
    {
        var k1 = PinLayout.ContentScale(PinLayout.PhysicalSize(ImageWidth, ImageHeight, 1.0).Width, 1.0, ImageWidth);
        var k2 = PinLayout.ContentScale(PinLayout.PhysicalSize(ImageWidth, ImageHeight, 2.0).Width, 1.0, ImageWidth);

        Assert.Equal(1.0, k1, 6);
        Assert.Equal(2.0, k2, 6);
        Assert.Equal(240.0, 120 * k2, 6); // 图像 x=120 在 2 倍时落在 DIP 240
    }

    [Fact]
    public void 降采样比例与_k_是两件独立的事_先还原再乘_k()
    {
        // 识别前按 0.5 缩小：OCR 坐标属于缩小图 → 先 ×(1/0.5) 回到原裁剪图，再 ×k 换算到 DIP
        var fitRatio = 0.5;
        var k = PinLayout.ContentScale(PinLayout.PhysicalSize(ImageWidth, ImageHeight, 1.5).Width, 1.0, ImageWidth);

        var ocrLeftInDownscaled = 60.0;
        var originalCropLeft = ocrLeftInDownscaled / fitRatio;
        var dipLeft = originalCropLeft * k;

        Assert.Equal(120, originalCropLeft, 6);
        Assert.Equal(180, dipLeft, 6);
    }

    [Fact]
    public void 面板只影响窗口高度_不影响图片的水平映射()
    {
        var panel = 200;
        var (width, height) = PinLayout.PhysicalSizeWithPanel(ImageWidth, ImageHeight, 1.0, panel);
        var k = PinLayout.ContentScale(width, 1.0, ImageWidth);

        Assert.Equal(1.0, k, 6); // 水平方向与无面板时完全一致
        Assert.Equal(ImageHeight + panel, height);
    }
}
