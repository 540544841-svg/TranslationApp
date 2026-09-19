using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 覆盖底色取样（FR-027 / 14.3.4）：外环 40 点中位数、贴边时退化为框内直方图众数、
/// busy 判定（外环亮度标准差 &gt; 24）与取样失败的回退（界面用 <c>Brush.Overlay.CoverFallback</c> 令牌）。
/// </summary>
public sealed class BackgroundSamplerTests
{
    private const int Width = 120;

    private const int Height = 60;

    private static byte[] Fill(byte b, byte g, byte r)
    {
        var buffer = new byte[Width * Height * 4];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = 255;
        }

        return buffer;
    }

    private static byte[] Checkerboard(byte b1, byte g1, byte r1, byte b2, byte g2, byte r2, int cell = 2)
    {
        var buffer = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var first = ((x / cell) + (y / cell)) % 2 == 0;
                var index = ((y * Width) + x) * 4;
                buffer[index] = first ? b1 : b2;
                buffer[index + 1] = first ? g1 : g2;
                buffer[index + 2] = first ? r1 : r2;
                buffer[index + 3] = 255;
            }
        }

        return buffer;
    }

    private static PixelRect Area(int x = 30, int y = 20, int width = 40, int height = 16) =>
        new(x, y, width, height);

    [Fact]
    public void 白底_取样成功_亮度接近_255_且不_busy()
    {
        var sample = BackgroundSampler.Sample(Fill(255, 255, 255), Width, Height, Area());

        Assert.True(sample.Success);
        Assert.Equal(0xFFFFFFFFu, sample.Argb);
        Assert.True(sample.Luma > 240);
        Assert.False(sample.IsBusy);
    }

    [Fact]
    public void 深底_取样成功_亮度低_不_busy_且_ARGB_正确()
    {
        var sample = BackgroundSampler.Sample(Fill(0x16, 0x16, 0x16), Width, Height, Area());

        Assert.True(sample.Success);
        Assert.Equal(0xFF161616u, sample.Argb);
        Assert.True(sample.Luma < 30);
        Assert.False(sample.IsBusy);
    }

    [Fact]
    public void 花底_外环标准差大_判为_busy()
    {
        var buffer = Checkerboard(0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 1);
        var sample = BackgroundSampler.Sample(buffer, Width, Height, Area());

        Assert.True(sample.Success);
        Assert.True(sample.Std > BackgroundSampler.BusyStdThreshold);
        Assert.True(sample.IsBusy);
    }

    [Fact]
    public void 缓冲太小或区域为空_取样失败_Argb_为_null()
    {
        var tooSmall = BackgroundSampler.Sample(new byte[16], Width, Height, Area());
        Assert.False(tooSmall.Success);
        Assert.Null(tooSmall.Argb);

        var empty = BackgroundSampler.Sample(Fill(255, 255, 255), Width, Height, new PixelRect(0, 0, 0, 0));
        Assert.False(empty.Success);
        Assert.Null(empty.Argb);
    }

    [Fact]
    public void 段框贴到边界_外环越界_退化为框内直方图众数仍能成功()
    {
        // 段框贴着左上角：外环有一半以上在图像外
        var sample = BackgroundSampler.Sample(Fill(200, 200, 200), Width, Height, new PixelRect(0, 0, 30, 12));

        Assert.True(sample.Success);
        Assert.Equal(0xFFC8C8C8u, sample.Argb);
    }

    [Fact]
    public void BusyRatio_按占比判定_超过_40_才整体降级()
    {
        var busy = new BackgroundSample(true, 0, 0, 0, 10, 40, true);
        var calm = new BackgroundSample(true, 255, 255, 255, 250, 1, false);

        Assert.False(BackgroundSampler.IsMostlyBusy([]));
        Assert.False(BackgroundSampler.IsMostlyBusy([calm, calm, calm, busy, busy]));
        Assert.False(BackgroundSampler.IsMostlyBusy(
            [calm, calm, calm, calm, calm, calm, calm, busy, busy, busy])); // 30%
        Assert.True(BackgroundSampler.IsMostlyBusy([busy, busy, busy, calm, calm])); // 60%
    }

    [Fact]
    public void BusyRatio_空集合为_0() => Assert.Equal(0, BackgroundSampler.BusyRatio([]));

    [Fact]
    public void DominantArgb_取亮度中位数的那个底色_全失败则_null()
    {
        Assert.Null(BackgroundSampler.DominantArgb([]));
        Assert.Null(BackgroundSampler.DominantArgb([BackgroundSample.Failed, BackgroundSample.Failed]));

        var light = new BackgroundSample(true, 0xFF, 0xFF, 0xFF, 250, 0, false);
        var dark = new BackgroundSample(true, 0x10, 0x10, 0x10, 12, 0, false);
        // 两个样本取亮度中位（较亮者 = 序号 1）；失败样本不参与
        Assert.Equal(0xFFFFFFFFu, BackgroundSampler.DominantArgb([light, dark, BackgroundSample.Failed]));
    }

    [Fact]
    public void SampleAll_与块一一对应_同序()
    {
        var blocks = new[]
        {
            new OcrBlock(0, [0], new OcrRect(30, 20, 70, 36), new OcrRect(30, 20, 70, 36), "a"),
            new OcrBlock(1, [1], new OcrRect(30, 20, 70, 36), new OcrRect(0, 0, 4, 4), "b"),
        };

        var samples = BackgroundSampler.SampleAll(Fill(255, 255, 255), Width, Height, blocks);

        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.True(sample.Success));
    }

    [Fact]
    public void SampleAll_空块列表返回空() =>
        Assert.Empty(BackgroundSampler.SampleAll(Fill(0, 0, 0), Width, Height, []));

    [Fact]
    public void 失败样本的_ARGB_为_null_界面据此回退兜底令牌()
    {
        Assert.Null(BackgroundSample.Failed.Argb);
        Assert.False(BackgroundSample.Failed.Success);
        Assert.False(BackgroundSample.Failed.IsBusy);
    }
}
