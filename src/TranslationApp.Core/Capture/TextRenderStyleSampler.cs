namespace TranslationApp.Core.Capture;

/// <summary>
/// 文字墨色取样（FR-048 / 批 4 spec §6）：在段框**内部**做颜色聚类，
/// 把与底色（<c>coverArgb</c>，由 <see cref="BackgroundSampler"/> 给出）量化同簇的像素视为背景，
/// 剩余像素里占比最高、且与底色亮度差达标的簇 = 文字色；不达标返回 null（沿用黑/白令牌兜底）。
/// 纯字节运算、可单测；复杂底纹（最大墨簇占比不足）宁缺毋滥。
/// </summary>
public static class TextRenderStyleSampler
{
    /// <summary>墨簇最低占比：低于它认为框内没有稳定的"文字颜色"（照片底/多色混排）。</summary>
    public const double MinInkRatio = 0.08;

    /// <summary>墨簇与底色的最低相对亮度差（WCAG 0~1）：低于它换色没有可读性收益。</summary>
    public const double MinLumaDelta = 0.30;

    /// <summary>每 N 像素取 1 个样本（大框提速；OCR 段框本就小）。</summary>
    private const int StridePx = 2;

    private const int QuantizeShift = 4; // 8bit → 4bit/通道 = 4096 桶

    /// <summary>
    /// 采样框内文字色。area 越界/为空、底色取样失败、无合格墨簇 → null。
    /// 返回不透明 ARGB（画刷直接用，alpha 恒 0xFF）。
    /// </summary>
    public static uint? SampleInkArgb(byte[] bgra, int width, int height, PixelRect area, uint? coverArgb)
    {
        if (coverArgb is not { } cover || area.IsEmpty
            || bgra is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var left = Math.Max(0, area.X);
        var top = Math.Max(0, area.Y);
        var right = Math.Min(width, area.Right);
        var bottom = Math.Min(height, area.Bottom);
        if (right - left < 4 || bottom - top < 4)
        {
            return null;
        }

        var coverR = (byte)(cover >> 16);
        var coverG = (byte)(cover >> 8);
        var coverB = (byte)cover;
        var coverLuma = Luma(coverR, coverG, coverB);

        // 量化桶（4bit/通道 = 4096）计数 + RGB 累加，最后按桶还原均值色
        var counts = new int[4096];
        var sumR = new long[4096];
        var sumG = new long[4096];
        var sumB = new long[4096];
        var total = 0;

        for (var y = top; y < bottom; y += StridePx)
        {
            var rowOffset = y * width * 4;
            for (var x = left; x < right; x += StridePx)
            {
                var i = rowOffset + x * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var bucket = Bucket(r, g, b);
                counts[bucket]++;
                sumR[bucket] += r;
                sumG[bucket] += g;
                sumB[bucket] += b;
                total++;
            }
        }

        if (total == 0)
        {
            return null;
        }

        var coverBucket = Bucket(coverR, coverG, coverB);
        var best = -1;
        var bestCount = 0;
        for (var bucket = 0; bucket < counts.Length; bucket++)
        {
            // 与底色同量化桶（±1 容差按桶粗判：跳过同桶即可滤掉大部分背景像素）
            if (counts[bucket] == 0 || bucket == coverBucket)
            {
                continue;
            }

            var lumaDelta = Math.Abs(Luma(
                (byte)(sumR[bucket] / counts[bucket]),
                (byte)(sumG[bucket] / counts[bucket]),
                (byte)(sumB[bucket] / counts[bucket])) - coverLuma);
            if (lumaDelta < MinLumaDelta)
            {
                continue;
            }

            if (counts[bucket] > bestCount)
            {
                best = bucket;
                bestCount = counts[bucket];
            }
        }

        if (best < 0 || (double)bestCount / total < MinInkRatio)
        {
            return null;
        }

        var n = bestCount;
        return 0xFF000000u
               | ((uint)(sumR[best] / n) << 16)
               | ((uint)(sumG[best] / n) << 8)
               | (uint)(sumB[best] / n);
    }

    /// <summary>WCAG 相对亮度（0~1），与 <see cref="BackgroundSampler"/> 同一口径。</summary>
    private static double Luma(byte r, byte g, byte b) =>
        (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;

    private static int Bucket(byte r, byte g, byte b) =>
        ((r >> QuantizeShift) << 8) | ((g >> QuantizeShift) << 4) | (b >> QuantizeShift);
}
