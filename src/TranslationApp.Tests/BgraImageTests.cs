using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>截图缓冲的裁剪与缩放（13.2.3 步骤 7：不经 WPF 图像管线）。</summary>
public sealed class BgraImageTests
{
    private static byte[] MakeImage(int width, int height, Func<int, int, byte[]> pixel)
    {
        var buffer = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixel(x, y);
                var index = (y * width + x) * 4;
                buffer[index] = value[0];
                buffer[index + 1] = value[1];
                buffer[index + 2] = value[2];
                buffer[index + 3] = value[3];
            }
        }

        return buffer;
    }

    [Fact]
    public void Crop_ReturnsRequestedRegionWithTightStride()
    {
        // 4x3 图，像素值 = (x, y)
        var source = MakeImage(4, 3, (x, y) => [(byte)x, (byte)y, 0, 0]);

        var cropped = BgraImage.Crop(source, 4, 3, new PixelRect(1, 1, 2, 2));

        Assert.Equal(2 * 2 * 4, cropped.Length);
        // 裁剪后左上角是原图 (1,1)
        Assert.Equal(1, cropped[0]);
        Assert.Equal(1, cropped[1]);
        // 第二行第一列是原图 (1,2)
        Assert.Equal(1, cropped[8]);
        Assert.Equal(2, cropped[9]);
    }

    [Fact]
    public void Crop_ClampsRectToImageBounds()
    {
        var source = MakeImage(4, 3, (_, _) => [10, 20, 30, 0]);

        var cropped = BgraImage.Crop(source, 4, 3, new PixelRect(3, 2, 100, 100));

        Assert.Equal(1 * 1 * 4, cropped.Length);
        Assert.Equal(10, cropped[0]);
    }

    [Fact]
    public void Crop_WithEmptyRect_ReturnsEmpty()
    {
        var source = MakeImage(4, 3, (_, _) => [0, 0, 0, 0]);

        Assert.Empty(BgraImage.Crop(source, 4, 3, new PixelRect(0, 0, 0, 0)));
    }

    [Fact]
    public void Resize_DownscaleByHalf_AveragesEachTwoByTwoBlock()
    {
        // 2x2 图：左上 (0,0,0)，右上 (200,0,0)，左下 (0,100,0)，右下 (0,0,60)
        var source = MakeImage(2, 2, (x, y) => x == 0
            ? (y == 0 ? (byte[])[0, 0, 0, 0] : [0, 100, 0, 0])
            : (y == 0 ? (byte[])[200, 0, 0, 0] : [0, 0, 60, 0]));

        var scaled = BgraImage.Resize(source, 2, 2, 1, 1);

        Assert.Equal(4, scaled.Length);
        Assert.Equal(50, scaled[0]);  // B: (0+200+0+0)/4
        Assert.Equal(25, scaled[1]);  // G: (0+0+100+0)/4
        Assert.Equal(15, scaled[2]);  // R: (0+0+0+60)/4
        Assert.Equal(255, scaled[3]); // 输出恒为不透明
    }

    [Fact]
    public void Resize_KeepsSizeWhenUnchanged()
    {
        var source = MakeImage(3, 2, (x, y) => [(byte)(x + y), 0, 0, 0]);

        var scaled = BgraImage.Resize(source, 3, 2, 3, 2);

        Assert.Equal(source, scaled);
    }

    [Fact]
    public void Resize_UpscaleProducesRequestedSize()
    {
        var source = MakeImage(2, 2, (_, _) => [40, 50, 60, 0]);

        var scaled = BgraImage.Resize(source, 2, 2, 4, 4);

        Assert.Equal(4 * 4 * 4, scaled.Length);
        Assert.Equal(40, scaled[0]);
        Assert.Equal(255, scaled[3]);
    }
}
