using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 覆盖层元素生成的纯逻辑（块 → 元素的矩形与字号，FR-027 / 14.3.6 的单一换算系数 k）。
/// 界面只把这里的结果套到 WPF 元素上，因此「译文与图片永不错位」这条不变式可以脱离窗口单测。
/// </summary>
public sealed class PinOverlayElementTests
{
    private static PinOverlayBlock Block(int x, int y, int width, int height, double fontPx = 0) =>
        new(new PixelRect(x, y, width, height), "译文", 0xFF202020u, fontPx);

    [Fact]
    public void 矩形与字号按同一系数_k_换算_因此缩放后与图片严格对齐()
    {
        var element = PinLayout.OverlayElement(Block(30, 20, 200, 40, fontPx: 12), 1.5);

        Assert.Equal(45, element.Left, 6);
        Assert.Equal(30, element.Top, 6);
        Assert.Equal(300, element.Width, 6);
        Assert.Equal(60, element.Height, 6);
        Assert.Equal(18, element.FontSize, 6);
    }

    [Fact]
    public void 缩放系数取自窗口宽度与图像宽度之比_随缩放线性变化()
    {
        var k = PinLayout.ContentScale(960, 1.0, 960);
        Assert.Equal(1.0, k, 6);

        // 150% 屏上 zoom = 1.0 时窗口物理宽仍是 960 px，但窗口 DIP 宽是 640 → k = 0.667
        Assert.Equal(0.667, PinLayout.ContentScale(960, 1.5, 960), 3);

        // 同一张图放大到 1920 px 宽：元素与字号都翻倍，元素与图片的相对位置不变
        var at1 = PinLayout.OverlayElement(Block(10, 10, 100, 20, 10), PinLayout.ContentScale(960, 1.0, 960));
        var at2 = PinLayout.OverlayElement(Block(10, 10, 100, 20, 10), PinLayout.ContentScale(1920, 1.0, 960));
        Assert.Equal(at1.Left * 2, at2.Left, 6);
        Assert.Equal(at1.Width * 2, at2.Width, 6);
        Assert.Equal(at1.FontSize * 2, at2.FontSize, 6);
    }

    [Fact]
    public void 字号缺失时按块高推算_与排版阶段的下限口径一致()
    {
        var element = PinLayout.OverlayElement(Block(0, 0, 100, 50, fontPx: 0), 1.0);

        Assert.Equal(50 * 0.42, element.FontSize, 6);
    }

    [Fact]
    public void 尺寸与字号一律保底_1px_不生成零宽零号的元素()
    {
        var element = PinLayout.OverlayElement(Block(0, 0, 0, 0, fontPx: 0), 0.0001);

        Assert.True(element.Width >= 1);
        Assert.True(element.Height >= 1);
        Assert.True(element.FontSize >= 1);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void 换算系数非法时返回全零_界面据此跳过绘制而不是画出错位的块(double k)
    {
        var element = PinLayout.OverlayElement(Block(30, 20, 200, 40, fontPx: 12), k);

        Assert.Equal(default, element);
    }
}
