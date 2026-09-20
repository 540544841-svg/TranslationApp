using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TranslationApp.Services;

/// <summary>解码后的 BGRA 位图（喂给 OcrService 的三元组）。</summary>
public sealed record DecodedImage(byte[] Bgra, int Width, int Height);

/// <summary>
/// 图片文件 → BGRA 解码器（FR-047）：WPF 成像管线（无新依赖），输出直接喂
/// <see cref="OcrService"/>；长边超过 <paramref name="maxDimension"/> 时等比缩小
/// （与截图链路同一像素上限，防大图打爆 OCR）。任何解码失败抛异常，由批量编排计为单张失败。
/// </summary>
public static class ImageFileDecoder
{
    public static DecodedImage Decode(string path, int maxDimension)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // 读完即释放文件句柄，不锁源文件
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
        {
            throw new InvalidDataException("图片尺寸为 0");
        }

        BitmapSource source = bitmap;
        var longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (maxDimension > 0 && longest > maxDimension)
        {
            var scale = (double)maxDimension / longest;
            var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            scaled.Freeze();
            source = scaled;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return new DecodedImage(pixels, width, height);
    }
}
