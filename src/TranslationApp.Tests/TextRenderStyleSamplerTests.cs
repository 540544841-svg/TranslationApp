using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-048：段框内文字墨色取样（合成像素，零 IO）。</summary>
public class TextRenderStyleSamplerTests
{
    private const uint White = 0xFFFFFFFF;
    private const uint Black = 0xFF000000;

    /// <summary>w×h 全白底，中间横条涂彩色"文字"。</summary>
    private static byte[] Canvas(int w, int h, uint ink, int inkTop, int inkBottom)
    {
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = y >= inkTop && y < inkBottom ? ink : White;
                var i = (y * w + x) * 4;
                px[i] = (byte)(c & 0xFF);
                px[i + 1] = (byte)((c >> 8) & 0xFF);
                px[i + 2] = (byte)((c >> 16) & 0xFF);
                px[i + 3] = 0xFF;
            }
        }

        return px;
    }

    [Fact]
    public void DarkInk_OnLightBackground_ReturnsDarkColor()
    {
        var px = Canvas(40, 20, 0xFF202020, 6, 14);
        var ink = TextRenderStyleSampler.SampleInkArgb(
            px, 40, 20, new PixelRect(0, 0, 40, 20), White);
        Assert.NotNull(ink);
        var v = ink!.Value;
        Assert.True((byte)(v >> 16) < 96 && (byte)(v >> 8) < 96 && (byte)v < 96);
    }

    [Fact]
    public void LightInk_OnDarkBackground_ReturnsLightColor()
    {
        // 深底浅字：底色黑、文字近白
        var px = new byte[40 * 20 * 4];
        for (var y = 6; y < 14; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = 0xEE;
                px[i + 3] = 0xFF;
            }
        }

        var ink = TextRenderStyleSampler.SampleInkArgb(
            px, 40, 20, new PixelRect(0, 0, 40, 20), Black);
        Assert.NotNull(ink);
        Assert.True((byte)(ink!.Value >> 16) > 160);
    }

    [Fact]
    public void LowContrast_ReturnsNull()
    {
        // 浅灰字（亮度差不足 0.30）→ 不换色，沿用令牌
        var px = Canvas(40, 20, 0xFFD8D8D8, 6, 14);
        Assert.Null(TextRenderStyleSampler.SampleInkArgb(
            px, 40, 20, new PixelRect(0, 0, 40, 20), White));
    }

    [Fact]
    public void InkTooRare_ReturnsNull()
    {
        // 墨色占比 <8%（照片底纹场景）→ 宁缺毋滥
        var px = Canvas(40, 20, White, 0, 0);
        for (var y = 2; y < 4; y++)
        {
            for (var x = 2; x < 4; x++)
            {
                var i = (y * 40 + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = 0x00;
            }
        }

        Assert.Null(TextRenderStyleSampler.SampleInkArgb(
            px, 40, 20, new PixelRect(0, 0, 40, 20), White));
    }

    [Fact]
    public void MissingCoverOrEmptyArea_ReturnsNull()
    {
        var px = Canvas(40, 20, 0xFF202020, 6, 14);
        Assert.Null(TextRenderStyleSampler.SampleInkArgb(px, 40, 20, new PixelRect(0, 0, 40, 20), null));
        Assert.Null(TextRenderStyleSampler.SampleInkArgb(px, 40, 20, new PixelRect(0, 0, 0, 0), White));
        // 区域整体越界
        Assert.Null(TextRenderStyleSampler.SampleInkArgb(px, 40, 20, new PixelRect(100, 100, 10, 10), White));
    }
}
