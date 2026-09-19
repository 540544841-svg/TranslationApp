using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 截图坐标换算（13.2.3 硬性要求：遮罩内 DIP → 图像物理像素）与超限缩放决策。
/// 覆盖 100% / 150% / 200% / 125% 与非整数结果，作为混合 DPI 错位的回归防线。
/// </summary>
public sealed class CaptureGeometryTests
{
    [Theory]
    [InlineData(1000.0, 1000.0, 1.0)]   // 100%
    [InlineData(1920.0, 1280.0, 1.5)]   // 150%
    [InlineData(2560.0, 1280.0, 2.0)]   // 200%
    [InlineData(1600.0, 1280.0, 1.25)]  // 125%
    public void ComputeScale_UsesMeasuredRatio(double physical, double dip, double expected) =>
        Assert.Equal(expected, CaptureGeometry.ComputeScale(physical, dip), 10);

    [Fact]
    public void ComputeScale_WithUnmeasuredWindow_FallsBackToOne()
    {
        // 窗口尚未布局（ActualWidth=0）时不能除零，退化为 1:1
        Assert.Equal(1.0, CaptureGeometry.ComputeScale(1920, 0));
        Assert.Equal(1.0, CaptureGeometry.ComputeScale(0, 1280));
    }

    [Theory]
    [InlineData(4.0, 4.0, true)]
    [InlineData(3.9, 40.0, false)]
    [InlineData(40.0, 3.9, false)]
    [InlineData(0.0, 0.0, false)]
    public void IsSelectionLargeEnough_EnforcesFourDipMinimum(double width, double height, bool expected) =>
        Assert.Equal(expected, CaptureGeometry.IsSelectionLargeEnough(width, height));

    [Fact]
    public void NormalizeDipRect_HandlesReverseDrag()
    {
        var rect = CaptureGeometry.NormalizeDipRect(300, 200, 100, 50);

        Assert.Equal(100, rect.X);
        Assert.Equal(50, rect.Y);
        Assert.Equal(200, rect.Width);
        Assert.Equal(150, rect.Height);
    }

    [Fact]
    public void DipToImageRect_At100Percent_IsIdentity()
    {
        var rect = CaptureGeometry.DipToImageRect(new DipRect(10, 20, 100, 40), 1.0, 1.0, 1920, 1080);

        Assert.Equal(new PixelRect(10, 20, 100, 40), rect);
    }

    [Fact]
    public void DipToImageRect_At150Percent_ScalesOriginAndSize()
    {
        // 150% 屏：1280 DIP 宽 → 1920 物理像素，比值 1.5
        var rect = CaptureGeometry.DipToImageRect(new DipRect(10, 10, 20, 20), 1.5, 1.5, 1920, 1080);

        Assert.Equal(new PixelRect(15, 15, 30, 30), rect);
    }

    [Fact]
    public void DipToImageRect_At200Percent_ScalesOriginAndSize()
    {
        var rect = CaptureGeometry.DipToImageRect(new DipRect(7, 3, 10, 5), 2.0, 2.0, 2560, 1440);

        Assert.Equal(new PixelRect(14, 6, 20, 10), rect);
    }

    [Fact]
    public void DipToImageRect_ClampsToImageBounds()
    {
        // 拖到窗口右下角外侧：结果必须收进图像内且宽高不为 0
        var rect = CaptureGeometry.DipToImageRect(new DipRect(950, 500, 200, 200), 1.0, 1.0, 1000, 600);

        Assert.Equal(new PixelRect(950, 500, 50, 100), rect);
    }

    [Fact]
    public void DipToImageRect_WithInvalidImage_ReturnsEmpty()
    {
        var rect = CaptureGeometry.DipToImageRect(new DipRect(0, 0, 10, 10), 1.0, 1.0, 0, 0);

        Assert.True(rect.IsEmpty);
    }

    [Fact]
    public void FitToMaxDimension_BelowLimit_KeepsOriginalSize()
    {
        var (width, height, ratio, downscaled) = CaptureGeometry.FitToMaxDimension(1200, 800, 10000);

        Assert.Equal(1200, width);
        Assert.Equal(800, height);
        Assert.Equal(1.0, ratio);
        Assert.False(downscaled);
    }

    [Fact]
    public void FitToMaxDimension_AboveLimit_ScalesLongestSideToLimit()
    {
        // 超长边 20000 → 10000，比例 0.5
        var (width, height, ratio, downscaled) = CaptureGeometry.FitToMaxDimension(20000, 10000, 10000);

        Assert.Equal(10000, width);
        Assert.Equal(5000, height);
        Assert.Equal(0.5, ratio);
        Assert.True(downscaled);
    }

    [Fact]
    public void FitToMaxDimension_TallImage_ScalesByHeight()
    {
        var (width, height, ratio, downscaled) = CaptureGeometry.FitToMaxDimension(5000, 20000, 10000);

        Assert.Equal(2500, width);
        Assert.Equal(10000, height);
        Assert.Equal(0.5, ratio);
        Assert.True(downscaled);
    }

    [Fact]
    public void FitToMaxDimension_KeepsAtLeastOnePixel()
    {
        var (width, height, _, downscaled) = CaptureGeometry.FitToMaxDimension(40000, 3, 10);

        Assert.Equal(10, width);
        Assert.Equal(1, height);
        Assert.True(downscaled);
    }
}
