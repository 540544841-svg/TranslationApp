namespace TranslationApp.Core.Capture;

/// <summary>
/// BGR32 / BGRA32 字节缓冲（内存顺序 B,G,R,A）的裁剪与缩放（13.2.3 步骤 7）。
/// 刻意不经过 WPF 图像管线：GDI 截屏得到的 alpha 通道不可信（13.2.2 陷阱①），
/// 由字节层直接处理既可以保真，也能被单元测试覆盖。
/// </summary>
public static class BgraImage
{
    public const int BytesPerPixel = 4;

    /// <summary>按矩形裁剪（自动 Clamp 到源图边界）；矩形无效时返回空数组。</summary>
    public static byte[] Crop(byte[] source, int sourceWidth, int sourceHeight, PixelRect rect)
    {
        if (source is null || sourceWidth <= 0 || sourceHeight <= 0 || rect.IsEmpty)
        {
            return [];
        }

        var left = Math.Clamp(rect.X, 0, sourceWidth);
        var top = Math.Clamp(rect.Y, 0, sourceHeight);
        var right = Math.Clamp(rect.Right, left, sourceWidth);
        var bottom = Math.Clamp(rect.Bottom, top, sourceHeight);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var sourceStride = sourceWidth * BytesPerPixel;
        var destinationStride = width * BytesPerPixel;
        var destination = new byte[destinationStride * height];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                source,
                (top + row) * sourceStride + left * BytesPerPixel,
                destination,
                row * destinationStride,
                destinationStride);
        }

        return destination;
    }

    /// <summary>
    /// 等比重采样（面积平均，缩小不丢字、放大用最近邻语义的均值退化）：
    /// 输出 alpha 一律写 255，得到一张不透明的合法 BGRA32 图。
    /// </summary>
    public static byte[] Resize(byte[] source, int width, int height, int newWidth, int newHeight)
    {
        if (source is null || width <= 0 || height <= 0 || newWidth <= 0 || newHeight <= 0)
        {
            return [];
        }

        var destination = new byte[newWidth * newHeight * BytesPerPixel];
        if (newWidth == width && newHeight == height)
        {
            Buffer.BlockCopy(source, 0, destination, 0, Math.Min(source.Length, destination.Length));
            return destination;
        }

        for (var y = 0; y < newHeight; y++)
        {
            var sourceY0 = y * height / newHeight;
            var sourceY1 = Math.Max(sourceY0 + 1, (y + 1) * height / newHeight);
            for (var x = 0; x < newWidth; x++)
            {
                var sourceX0 = x * width / newWidth;
                var sourceX1 = Math.Max(sourceX0 + 1, (x + 1) * width / newWidth);

                long blue = 0, green = 0, red = 0;
                var count = 0;
                for (var sy = sourceY0; sy < sourceY1; sy++)
                {
                    var rowOffset = sy * width * BytesPerPixel;
                    for (var sx = sourceX0; sx < sourceX1; sx++)
                    {
                        var index = rowOffset + sx * BytesPerPixel;
                        blue += source[index];
                        green += source[index + 1];
                        red += source[index + 2];
                        count++;
                    }
                }

                var target = (y * newWidth + x) * BytesPerPixel;
                destination[target] = (byte)(blue / count);
                destination[target + 1] = (byte)(green / count);
                destination[target + 2] = (byte)(red / count);
                destination[target + 3] = 255;
            }
        }

        return destination;
    }

    /// <summary>
    /// 双线性重采样（FR-029-1 预处理放大用，14.3.12.4）：放大时在源像素间插值，
    /// 避免 <see cref="Resize"/> 面积平均在放大时退化成块状、破坏小字抗锯齿信息。
    /// 采样点取目标像素中心反算的源坐标（半像素对齐），越界侧钳到边缘；输出 alpha 一律写 255。
    /// </summary>
    public static byte[] ResizeBilinear(byte[] source, int width, int height, int newWidth, int newHeight)
    {
        if (source is null || width <= 0 || height <= 0 || newWidth <= 0 || newHeight <= 0
            || source.Length < (long)width * height * BytesPerPixel)
        {
            return [];
        }

        var destination = new byte[newWidth * newHeight * BytesPerPixel];
        if (newWidth == width && newHeight == height)
        {
            Buffer.BlockCopy(source, 0, destination, 0, Math.Min(source.Length, destination.Length));
            return destination;
        }

        // 每个目标列的源坐标与插值权重只算一次（4 MP 目标图的内层循环省掉重复的浮点除法）
        var sourceX0 = new int[newWidth];
        var sourceX1 = new int[newWidth];
        var weightX = new double[newWidth];
        for (var x = 0; x < newWidth; x++)
        {
            var position = (x + 0.5) * width / (double)newWidth - 0.5;
            var floor = Math.Floor(position);
            weightX[x] = position - floor;
            sourceX0[x] = (int)Math.Clamp(floor, 0, width - 1);
            sourceX1[x] = (int)Math.Clamp(floor + 1, 0, width - 1);
        }

        for (var y = 0; y < newHeight; y++)
        {
            var position = (y + 0.5) * height / (double)newHeight - 0.5;
            var floor = Math.Floor(position);
            var weightY = position - floor;
            var y0 = (int)Math.Clamp(floor, 0, height - 1);
            var y1 = (int)Math.Clamp(floor + 1, 0, height - 1);
            var row0 = y0 * width * BytesPerPixel;
            var row1 = y1 * width * BytesPerPixel;

            for (var x = 0; x < newWidth; x++)
            {
                var i00 = row0 + sourceX0[x] * BytesPerPixel;
                var i01 = row0 + sourceX1[x] * BytesPerPixel;
                var i10 = row1 + sourceX0[x] * BytesPerPixel;
                var i11 = row1 + sourceX1[x] * BytesPerPixel;
                var fx = weightX[x];

                var target = (y * newWidth + x) * BytesPerPixel;
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = source[i00 + channel] + (source[i01 + channel] - source[i00 + channel]) * fx;
                    var bottom = source[i10 + channel] + (source[i11 + channel] - source[i10 + channel]) * fx;
                    destination[target + channel] = (byte)Math.Round(
                        top + (bottom - top) * weightY, MidpointRounding.AwayFromZero);
                }

                destination[target + 3] = 255;
            }
        }

        return destination;
    }
}
