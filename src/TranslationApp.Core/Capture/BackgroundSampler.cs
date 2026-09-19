namespace TranslationApp.Core.Capture;

/// <summary>
/// 一个覆盖区的底色取样结果（FR-027 / 14.3.4）。<see cref="Argb"/> 为 <c>null</c> 表示取样失败，
/// 界面据此回退到固定色令牌 <c>Brush.Overlay.CoverFallback</c>（Core 里没有任何颜色常量）。
/// </summary>
/// <param name="Success">是否取到可用底色。</param>
/// <param name="B">底色通道（外环中位数）。</param>
/// <param name="G">底色通道。</param>
/// <param name="R">底色通道。</param>
/// <param name="Luma">底色相对亮度（WCAG，用于「浅底配深字 / 深底配浅字」）。</param>
/// <param name="Std">外环采样亮度的总体标准差（&gt; <see cref="BackgroundSampler.BusyStdThreshold"/> 视为背景复杂）。</param>
/// <param name="IsBusy">背景是否复杂（渐变 / 照片 / 纹理）。</param>
public readonly record struct BackgroundSample(
    bool Success, byte B, byte G, byte R, double Luma, double Std, bool IsBusy)
{
    /// <summary>覆盖色（不透明 ARGB）；取样失败返回 null → 界面用兜底令牌。</summary>
    public uint? Argb => Success
        ? 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B
        : null;

    /// <summary>取样失败（越界 / 区域为空）。</summary>
    public static BackgroundSample Failed { get; } = new(false, 0, 0, 0, 0, 0, false);
}

/// <summary>
/// 覆盖底色取样（FR-027 / 14.3.4，纯字节运算，可单测）：
/// 在每个段框**外围 2~6 px 的一圈**均匀取 40 个采样点，对 B/G/R 三通道分别取**中位数**（抗噪优于均值）；
/// 外环不可用（段框贴边 / 越界点过半）时退化为段框内**颜色直方图众数**（每通道量化到 16 级）。
/// 底色亮度用 WCAG 相对亮度算（文字颜色与 busy 判定都基于它）。
/// </summary>
public static class BackgroundSampler
{
    /// <summary>外环采样点数（文档 14.3.4 的 40 点）。</summary>
    public const int SampleCount = 40;

    /// <summary>外环内边界（px，相对段框）。</summary>
    public const int RingInnerPx = 2;

    /// <summary>外环外边界（px，相对段框）。</summary>
    public const int RingOuterPx = 6;

    /// <summary>「背景复杂」判定阈值：外环采样亮度标准差 &gt; 24。</summary>
    public const double BusyStdThreshold = 24.0;

    /// <summary>busy 段占比超过该比例 → 整张降级 SidePanel（不硬画补丁，避免「一眼假」）。</summary>
    public const double BusyRatioLimit = 0.40;

    /// <summary>直方图量化的级数（每通道）。</summary>
    public const int QuantizeLevels = 16;

    /// <summary>对单个区域取样。失败（区域为空 / 缓冲尺寸不足 / 越界）返回 <see cref="BackgroundSample.Failed"/>。</summary>
    public static BackgroundSample Sample(byte[] bgra, int width, int height, PixelRect area)
    {
        if (bgra is null || width <= 0 || height <= 0
            || bgra.Length < (long)width * height * BgraImage.BytesPerPixel
            || area.IsEmpty)
        {
            return BackgroundSample.Failed;
        }

        var left = Math.Clamp(area.X, 0, width - 1);
        var top = Math.Clamp(area.Y, 0, height - 1);
        var right = Math.Clamp(area.Right, left + 1, width);
        var bottom = Math.Clamp(area.Bottom, top + 1, height);

        var ring = SampleRing(bgra, width, height, left, top, right, bottom, out var valid);
        if (valid * 2 >= SampleCount)
        {
            return ring;
        }

        // 外环不可用（段框贴到图像边界）→ 段框内颜色直方图众数
        var interior = SampleInteriorMode(bgra, width, left, top, right, bottom);
        return interior.Success || !ring.Success ? interior : ring;
    }

    /// <summary>对每个块按 <see cref="OcrBlock.CoverRect"/> 取样（与块一一对应、同序）。</summary>
    public static IReadOnlyList<BackgroundSample> SampleAll(
        byte[] bgra, int width, int height, IReadOnlyList<OcrBlock> blocks)
    {
        if (blocks is null || blocks.Count == 0)
        {
            return [];
        }

        var samples = new BackgroundSample[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            samples[i] = Sample(bgra, width, height, blocks[i].CoverRect.ToPixelRect());
        }

        return samples;
    }

    /// <summary>busy 段占比。</summary>
    public static double BusyRatio(IReadOnlyList<BackgroundSample> samples) =>
        samples is null || samples.Count == 0
            ? 0
            : (double)samples.Count(sample => sample.IsBusy) / samples.Count;

    /// <summary>busy 段占比是否已超过 <see cref="BusyRatioLimit"/>（超过即整张降级 SidePanel）。</summary>
    public static bool IsMostlyBusy(IReadOnlyList<BackgroundSample> samples) =>
        BusyRatio(samples) > BusyRatioLimit;

    /// <summary>全体取样中出现最多的底色（整块排版时统一底板用）；全部失败返回 null。</summary>
    public static uint? DominantArgb(IReadOnlyList<BackgroundSample> samples)
    {
        var usable = samples?.Where(sample => sample.Success).ToArray() ?? [];
        if (usable.Length == 0)
        {
            return null;
        }

        // 用「亮度中位数最接近的那个样本」而不是众数：颜色是连续量，众数在取样结果里几乎恒为 1
        var ordered = usable.OrderBy(sample => sample.Luma).ToArray();
        return ordered[ordered.Length / 2].Argb;
    }

    /// <summary>外环 40 点采样：每点取 B/G/R，三通道各取中位数；亮度标准差用于 busy 判定。</summary>
    private static BackgroundSample SampleRing(
        byte[] bgra, int width, int height, int left, int top, int right, int bottom, out int valid)
    {
        Span<byte> blues = stackalloc byte[SampleCount];
        Span<byte> greens = stackalloc byte[SampleCount];
        Span<byte> reds = stackalloc byte[SampleCount];
        Span<double> lumas = stackalloc double[SampleCount];
        valid = 0;

        // 环上参数化取点：沿「段框外扩 RingOuterPx 的矩形」周长等分，深度在 2/4/6 px 之间轮换
        var ox0 = left - RingOuterPx;
        var oy0 = top - RingOuterPx;
        var ox1 = right + RingOuterPx;
        var oy1 = bottom + RingOuterPx;
        var perimeter = 2.0 * ((ox1 - ox0) + (oy1 - oy0));
        if (perimeter <= 0)
        {
            return BackgroundSample.Failed;
        }

        for (var i = 0; i < SampleCount; i++)
        {
            var depth = RingInnerPx + (i % 3 * 2); // 2 / 4 / 6
            var (ax, ay) = PointOnRing(ox0, oy0, ox1, oy1, i / (double)SampleCount * perimeter);
            var (x, y) = PullInside(ax, ay, ox0, oy0, ox1, oy1, depth);
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                continue;
            }

            var index = ((y * width) + x) * BgraImage.BytesPerPixel;
            var b = bgra[index];
            var g = bgra[index + 1];
            var r = bgra[index + 2];
            blues[valid] = b;
            greens[valid] = g;
            reds[valid] = r;
            lumas[valid] = Luma(b, g, r);
            valid++;
        }

        if (valid == 0)
        {
            return BackgroundSample.Failed;
        }

        var medianB = Median(blues[..valid]);
        var medianG = Median(greens[..valid]);
        var medianR = Median(reds[..valid]);
        var luma = Luma(medianB, medianG, medianR);
        var mean = 0.0;
        for (var i = 0; i < valid; i++)
        {
            mean += lumas[i];
        }

        mean /= valid;
        var variance = 0.0;
        for (var i = 0; i < valid; i++)
        {
            variance += (lumas[i] - mean) * (lumas[i] - mean);
        }

        var std = Math.Sqrt(variance / valid);
        return new BackgroundSample(true, medianB, medianG, medianR, luma, std, std > BusyStdThreshold);
    }

    /// <summary>段框内的颜色直方图众数（每通道量化到 16 级），作为外环不可用时的兜底。</summary>
    private static BackgroundSample SampleInteriorMode(
        byte[] bgra, int width, int left, int top, int right, int bottom)
    {
        var step = Math.Max(1, (right - left) / 64);
        var buckets = new Dictionary<int, (int Count, long B, long G, long R, List<double> Lumas)>();
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x += step)
            {
                var index = ((y * width) + x) * BgraImage.BytesPerPixel;
                var b = bgra[index];
                var g = bgra[index + 1];
                var r = bgra[index + 2];
                var key = (Quantize(b) << 8) | (Quantize(g) << 4) | Quantize(r);
                var bucket = buckets.TryGetValue(key, out var existing)
                    ? existing
                    : (Count: 0, B: 0L, G: 0L, R: 0L, Lumas: new List<double>());
                bucket.Count++;
                bucket.B += b;
                bucket.G += g;
                bucket.R += r;
                bucket.Lumas.Add(Luma(b, g, r));
                buckets[key] = bucket;
            }
        }

        if (buckets.Count == 0)
        {
            return BackgroundSample.Failed;
        }

        var mode = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        var medianB = (byte)(mode.B / mode.Count);
        var medianG = (byte)(mode.G / mode.Count);
        var medianR = (byte)(mode.R / mode.Count);
        var mean = mode.Lumas.Average();
        var std = Math.Sqrt(mode.Lumas.Sum(value => (value - mean) * (value - mean)) / mode.Lumas.Count);
        return new BackgroundSample(
            true, medianB, medianG, medianR, Luma(medianB, medianG, medianR), std, std > BusyStdThreshold);
    }

    private static int Quantize(byte value) => value * QuantizeLevels / 256;

    /// <summary>把环上点沿指向段框中心的方向拉进 <paramref name="depth"/> px（2~6 px 的采样深度）。</summary>
    private static (int X, int Y) PullInside(int x, int y, int ox0, int oy0, int ox1, int oy1, int depth)
    {
        var cx = (ox0 + ox1) / 2.0;
        var cy = (oy0 + oy1) / 2.0;
        var dx = cx - x;
        var dy = cy - y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 0.001)
        {
            return (x, y);
        }

        return ((int)Math.Round(x + (dx / length * depth)), (int)Math.Round(y + (dy / length * depth)));
    }

    private static (int X, int Y) PointOnRing(int x0, int y0, int x1, int y1, double t)
    {
        var width = x1 - x0;
        var height = y1 - y0;
        if (t < width)
        {
            return (x0 + (int)t, y0);
        }

        t -= width;
        if (t < height)
        {
            return (x1, y0 + (int)t);
        }

        t -= height;
        if (t < width)
        {
            return (x1 - (int)t, y1);
        }

        return (x0, y1 - (int)(t - width));
    }

    private static double Luma(byte b, byte g, byte r) =>
        (0.114 * b) + (0.587 * g) + (0.299 * r);

    private static byte Median(Span<byte> values)
    {
        var copy = values.ToArray();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }
}
