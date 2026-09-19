using System.Globalization;

namespace TranslationApp.Core.Capture;

/// <summary>预处理三态（FR-029-1 / 14.3.12.4）：auto（默认，命中触发条件才增强重跑）/ on / off。</summary>
public enum OcrPreprocessAction
{
    /// <summary>设置为 off，仅原图识别。</summary>
    Disabled,

    /// <summary>auto 未命中触发条件，仅原图识别（零回归路径）。</summary>
    NotTriggered,

    /// <summary>增强候选胜出，采用增强识别结果（还原比例 = fit.Ratio / scale）。</summary>
    EnhancedUsed,

    /// <summary>增强重跑完成但原候选胜出，保持原候选。</summary>
    KeptOriginal,

    /// <summary>增强重跑无结果（null 或 0 行），保持原候选。</summary>
    NoEnhancedResult,

    /// <summary>增强路径异常（增强算子或重跑识别抛出），保持原候选（故障隔离）。</summary>
    FaultIsolated,
}

/// <summary>决策日志级别（Core 不依赖日志框架，由 App 层映射到 Serilog）。</summary>
public enum OcrPreprocessLogLevel
{
    Debug,
    Information,
    Warning,
}

/// <summary>
/// 预处理双跑的一次候选快照（Core 所有，无 WinRT/UI 依赖；App 层的 <c>OcrRecognition</c> 可无损往返映射）。
/// 坐标语义与 <see cref="OcrLineBox"/> 一致：属于「产生该候选的那张位图」——增强候选的位图是放大图，
/// 面积比较前必须按 <c>scale²</c> 归一（见 <see cref="OcrPreprocess.ShouldUseEnhanced"/>）。
/// </summary>
public sealed record OcrPreprocessCandidate(
    string? Text,
    IReadOnlyList<OcrLineBox> Lines,
    double? TextAngle = null,
    string? EngineTag = null)
{
    /// <summary>词框总面积（Σ 词框面积，属于该候选来源位图的像素空间）：覆盖率门控与触发条件②的输入。</summary>
    public double TotalWordBoxArea
    {
        get
        {
            double total = 0;
            foreach (var line in Lines)
            {
                foreach (var word in line.Words)
                {
                    if (word.Rect.Width > 0 && word.Rect.Height > 0)
                    {
                        total += word.Rect.Width * word.Rect.Height;
                    }
                }
            }

            return total;
        }
    }
}

/// <summary>预处理双跑的最终决策（候选 + 还原比例 + 决策日志内容；日志只含元数据，绝无识别内容）。</summary>
/// <param name="Candidate">最终采用的候选（原候选或增强候选）。</param>
/// <param name="RestoreRatio">该候选的还原比例：原候选 = fit.Ratio；增强候选 = fit.Ratio / scale（坐标零改动关键）。</param>
/// <param name="EnhancedUsed">是否采用增强候选。</param>
/// <param name="Scale">实际放大倍数（未重跑时为 null）。</param>
/// <param name="Action">决策动作。</param>
/// <param name="Level">决策日志级别。</param>
/// <param name="LogMessage">完整决策日志（沿用 auto 双跑择优的既有格式：得分/字符数/词框面积/命中条件，无内容）。</param>
/// <param name="Error">故障隔离时捕获的异常（供 App 层 Warning 日志附带），其余为 null。</param>
public sealed record OcrPreprocessOutcome(
    OcrPreprocessCandidate Candidate,
    double RestoreRatio,
    bool EnhancedUsed,
    double? Scale,
    OcrPreprocessAction Action,
    OcrPreprocessLogLevel Level,
    string LogMessage,
    Exception? Error = null);

/// <summary>
/// OCR 预处理增强（FR-029-1 / 14.3.12.4，纯函数可单测）。
///
/// 两段式管线：预处理在 OCR 前拿不到行高，图像级启发式不可靠，因此**原图识别先行**（完全等于既有行为），
/// 仅当命中触发条件（或设置 on）时对「2x 级放大 + 灰度对比度拉伸」的增强图重跑一次，
/// 两次候选按 <see cref="OcrScriptScoring.ShouldPreferSecondary"/> 择优（secondary = 增强候选）。
/// 增强候选的还原比例 = fit.Ratio / scale，几何管线（行框/扩边/聚块）一行不改。
/// </summary>
public static class OcrPreprocess
{
    // ==================== 设置三态 ====================

    public const string ModeAuto = "auto";
    public const string ModeOn = "on";
    public const string ModeOff = "off";

    /// <summary>未知值一律按默认 auto（旧配置缺字段向后兼容，AC 6）。</summary>
    public static string NormalizeMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        ModeOn => ModeOn,
        ModeOff => ModeOff,
        _ => ModeAuto,
    };

    // ==================== 触发条件与增强参数（阈值放 Core 常量，实测后调） ====================

    /// <summary>触发①：行高中位数低于该值视为小字（px，识别位图空间）。</summary>
    public const double SmallLineHeightPx = 14.0;

    /// <summary>触发②：内容字符数低于该值且词框面积占比低于 <see cref="LowContentAreaRatio"/>（零碎漏识）。</summary>
    public const int LowContentChars = 6;

    /// <summary>
    /// 触发②的词框总面积/选区面积低阈值。文档 14.3.12.4 只给「低阈值」未定初值，
    /// 这里取 5% 起步（低于该值说明选区内绝大多数墨迹未被框出），实测后调。
    /// </summary>
    public const double LowContentAreaRatio = 0.05;

    /// <summary>放大倍数上限（2x 级）。</summary>
    public const double MaxScale = 2.0;

    /// <summary>放大后位图像素上限（与 PinMaxPixelsPerImage 同量级，防止 BGRA 临时缓冲击穿内存）。</summary>
    public const int DefaultMaxPixels = 4_000_000;

    /// <summary>
    /// 触发判定（任一命中即增强重跑）：① 行高中位数 &lt; 14 px（小字）；② 内容 &lt; 6 字且词框面积占比过低（零碎漏识）。
    /// 无行时行高中位数退化（<see cref="OcrLayoutRules.MedianLineHeight"/> 返回 1），不能据此触发①，交给②判定。
    /// </summary>
    public static bool ShouldTrigger(OcrPreprocessCandidate primary, int width, int height, out string reason)
    {
        var lines = primary.Lines;
        var invariant = CultureInfo.InvariantCulture;
        if (lines.Count > 0)
        {
            var median = OcrLayoutRules.MedianLineHeight(lines);
            if (median < SmallLineHeightPx)
            {
                reason = string.Create(invariant,
                    $"行高中位数 {median:0.#} px < {SmallLineHeightPx:0.#} px（小字）");
                return true;
            }
        }

        var chars = OcrScriptScoring.ContentChars(primary.Text);
        var selectionArea = Math.Max(1, width) * (double)Math.Max(1, height);
        var areaRatio = primary.TotalWordBoxArea / selectionArea;
        if (chars < LowContentChars && areaRatio < LowContentAreaRatio)
        {
            reason = string.Create(invariant,
                $"内容 {chars} 字 < {LowContentChars} 且词框面积占比 {areaRatio:P1} < {LowContentAreaRatio:P1}（零碎漏识）");
            return true;
        }

        reason = string.Create(invariant,
            $"行高中位数 {(lines.Count > 0 ? OcrLayoutRules.MedianLineHeight(lines).ToString("0.#", invariant) : "无行")}（小字阈值 < {SmallLineHeightPx:0.#} px），"
            + $"内容 {chars} 字、词框面积占比 {areaRatio:P1}（漏识阈值：内容 < {LowContentChars} 字且占比 < {LowContentAreaRatio:P1}）");
        return false;
    }

    /// <summary>
    /// 等比放大倍数：<c>min(2.0, sqrt(maxPixels / 选区面积))</c>。
    /// 选区面积已超上限时平方根 &lt; 1，此时**不缩小**（下限钳到 1，只做对比度拉伸）——文档公式的边界澄清。
    /// </summary>
    public static double ComputeScale(int width, int height, int maxPixels = DefaultMaxPixels)
    {
        if (width <= 0 || height <= 0 || maxPixels <= 0)
        {
            return 1.0;
        }

        var scale = Math.Sqrt(maxPixels / ((double)width * height));
        return Math.Clamp(scale, 1.0, MaxScale);
    }

    /// <summary>增强候选的还原比例 = fit.Ratio / scale（scale 退化时直接返回 fit.Ratio）。</summary>
    public static double CombineRestoreRatio(double fitRatio, double scale)
    {
        var ratio = fitRatio > 0 ? fitRatio : 1.0;
        return scale > 0 ? ratio / scale : ratio;
    }

    /// <summary>
    /// 双跑择优（secondary = 增强候选）：直接复用 <see cref="OcrScriptScoring.ShouldPreferSecondary"/>。
    /// 关键：增强候选的词框属于放大后的位图，面积必须 ÷ scale² 归一回原位图空间再比较，
    /// 否则放大自带面积优势会让门控（条件②）形同虚设。
    /// </summary>
    public static bool ShouldUseEnhanced(OcrPreprocessCandidate primary, OcrPreprocessCandidate enhanced, double scale)
    {
        var scaleSquared = scale > 0 ? scale * scale : 1.0;
        return OcrScriptScoring.ShouldPreferSecondary(
            primary.Text,
            enhanced.Text,
            enhanced.TotalWordBoxArea / scaleSquared,
            primary.TotalWordBoxArea);
    }

    // ==================== 双跑管线（故障隔离：增强路径任何异常不得影响主链路） ====================

    /// <summary>增强图重跑的识别回调（App 层注入 <c>OcrService.RecognizeWithLayoutAsync</c> 的薄适配）。</summary>
    public delegate Task<OcrPreprocessCandidate?> OcrPreprocessRecognizer(byte[] bgra, int width, int height);

    /// <summary>
    /// 两段式决策：<paramref name="primary"/> 为原图识别结果（调用方已先行识别，零回归），
    /// 命中触发条件（auto）或设置 on 时构建增强图并调 <paramref name="recognize"/> 重跑，按择优规则返回最终候选。
    /// 增强路径整体 try/catch：异常或重跑无结果时用原候选，仅 Warning 日志（AC 3）。
    /// </summary>
    public static async Task<OcrPreprocessOutcome> RunAsync(
        OcrPreprocessCandidate? primary,
        byte[] sourceBgra,
        int width,
        int height,
        double fitRatio,
        string? mode,
        OcrPreprocessRecognizer recognize)
    {
        var ratio = fitRatio > 0 ? fitRatio : 1.0;
        if (primary is null)
        {
            // 原图识别已失败（引擎创建不出/缓冲不匹配）：增强无意义，保持既有失败路径（零回归）
            return new(primary ?? new OcrPreprocessCandidate(null, []), ratio, false, null,
                OcrPreprocessAction.NotTriggered, OcrPreprocessLogLevel.Debug,
                "原图识别无结果，跳过预处理");
        }

        var normalized = NormalizeMode(mode);
        if (normalized == ModeOff)
        {
            return new(primary, ratio, false, null,
                OcrPreprocessAction.Disabled, OcrPreprocessLogLevel.Debug,
                "预处理已禁用（off），仅原图识别");
        }

        if (normalized == ModeAuto && !ShouldTrigger(primary, width, height, out var triggerReason))
        {
            return new(primary, ratio, false, null,
                OcrPreprocessAction.NotTriggered, OcrPreprocessLogLevel.Information,
                $"未触发预处理：{triggerReason}");
        }

        // 增强重跑（整个增强路径被 try/catch 包裹）
        double scale;
        byte[]? enhanced;
        int newWidth, newHeight;
        try
        {
            scale = ComputeScale(width, height);
            enhanced = Enhance(sourceBgra, width, height, scale, out newWidth, out newHeight);
        }
        catch (Exception ex)
        {
            return FaultIsolated(primary, ratio, null, ex);
        }

        if (enhanced is null)
        {
            return FaultIsolated(primary, ratio, scale,
                new InvalidOperationException($"增强图构建失败（缓冲 {sourceBgra.Length} 字节，选区 {width}x{height}）"));
        }

        OcrPreprocessCandidate? secondary;
        try
        {
            secondary = await recognize(enhanced, newWidth, newHeight);
        }
        catch (Exception ex)
        {
            return FaultIsolated(primary, ratio, scale, ex);
        }

        if (secondary is null || secondary.Lines.Count == 0)
        {
            // 原候选本就无行（空画面）时无结果属预期，降为 Information；原候选有行而增强跑空才是告警
            var level = primary.Lines.Count > 0 ? OcrPreprocessLogLevel.Warning : OcrPreprocessLogLevel.Information;
            return new(primary, ratio, false, scale,
                OcrPreprocessAction.NoEnhancedResult, level,
                $"预处理增强重跑无结果（{newWidth}x{newHeight}，scale {scale:0.##}），保持原候选");
        }

        var metrics = FormatMetrics(primary, secondary, scale);
        var condition = OcrScriptScoring.DescribeDecision(
            primary.Text, secondary.Text,
            secondary.TotalWordBoxArea / (scale > 0 ? scale * scale : 1.0),
            primary.TotalWordBoxArea);
        if (!ShouldUseEnhanced(primary, secondary, scale))
        {
            return new(primary, ratio, false, scale,
                OcrPreprocessAction.KeptOriginal, OcrPreprocessLogLevel.Information,
                $"预处理增强重跑：保持原候选（scale {scale:0.##}；{metrics}；{condition}）");
        }

        return new(secondary, CombineRestoreRatio(ratio, scale), true, scale,
            OcrPreprocessAction.EnhancedUsed, OcrPreprocessLogLevel.Information,
            $"预处理增强重跑：增强候选胜出（scale {scale:0.##}，还原比例 {CombineRestoreRatio(ratio, scale):0.###}；{metrics}；{condition}）");
    }

    private static OcrPreprocessOutcome FaultIsolated(
        OcrPreprocessCandidate primary, double ratio, double? scale, Exception error) =>
        new(primary, ratio, false, scale,
            OcrPreprocessAction.FaultIsolated, OcrPreprocessLogLevel.Warning,
            $"预处理增强路径异常，保持原候选（{error.GetType().Name}: {error.Message}）", error);

    /// <summary>双跑决策指标（既有 auto 双跑日志格式；只含得分/字符数/词框面积，无识别内容）。</summary>
    private static string FormatMetrics(OcrPreprocessCandidate primary, OcrPreprocessCandidate secondary, double scale)
    {
        var scaleSquared = scale > 0 ? scale * scale : 1.0;
        return string.Create(CultureInfo.InvariantCulture,
            $"原候选得分 {OcrScriptScoring.Score(primary.Text):0.###}、内容 {OcrScriptScoring.ContentChars(primary.Text)} 字、"
            + $"词框 {primary.TotalWordBoxArea:0} px²；增强候选得分 {OcrScriptScoring.Score(secondary.Text):0.###}、"
            + $"内容 {OcrScriptScoring.ContentChars(secondary.Text)} 字、词框（还原后）{secondary.TotalWordBoxArea / scaleSquared:0} px²");
    }

    // ==================== 增强算子（第一批只做两个：放大 + 对比度拉伸） ====================

    /// <summary>
    /// 增强算子：灰度对比度拉伸（直方图 2%~98% 分位线性拉伸）+ 等比双线性放大（先拉伸后放大，直方图只需算一次）。
    /// 二值化不启用、反色为实验项默认关闭（14.3.12.4）。输出 alpha 一律 255（GDI 截屏的 alpha 不可信）。
    /// 返回 null 表示输入非法；<paramref name="scale"/> 会被钳制到 [1, <see cref="MaxScale"/>]。
    /// </summary>
    public static byte[]? Enhance(byte[]? bgra, int width, int height, double scale, out int newWidth, out int newHeight)
    {
        newWidth = 0;
        newHeight = 0;
        if (bgra is null || width <= 0 || height <= 0
            || bgra.Length < (long)width * height * BgraImage.BytesPerPixel)
        {
            return null;
        }

        var clamped = Math.Clamp(scale, 1.0, MaxScale);
        var stretched = StretchContrast(bgra, width, height);
        newWidth = (int)(width * clamped);
        newHeight = (int)(height * clamped);
        if (newWidth <= 0 || newHeight <= 0)
        {
            return null;
        }

        return newWidth == width && newHeight == height
            ? stretched
            : BgraImage.ResizeBilinear(stretched, width, height, newWidth, newHeight);
    }

    /// <summary>
    /// 灰度对比度拉伸：按 Rec.601 亮度做 256 级直方图，取 2%~98% 分位为 [lo, hi]，
    /// 对 B/G/R 三通道施加同一线性映射（保色相）；hi ≤ lo（常数图）时近似恒等返回副本。
    /// 对已高对比图像天然安全（分位 ≈ 0 与 255，映射近似恒等）。
    /// </summary>
    private static byte[] StretchContrast(byte[] bgra, int width, int height)
    {
        var pixels = width * height;
        Span<int> histogram = stackalloc int[256];
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * BgraImage.BytesPerPixel;
            var luma = (bgra[offset] * 299 + bgra[offset + 1] * 587 + bgra[offset + 2] * 114) / 1000;
            histogram[luma]++;
        }

        var lowThreshold = (int)Math.Ceiling(pixels * 0.02);
        var highThreshold = (int)Math.Ceiling(pixels * 0.98);
        int lo = 0, hi = 255, cumulative = 0;
        for (var value = 0; value < 256; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= lowThreshold)
            {
                lo = value;
                break;
            }
        }

        cumulative = 0;
        for (var value = 0; value < 256; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= highThreshold)
            {
                hi = value;
                break;
            }
        }

        var result = new byte[bgra.Length];
        if (hi <= lo)
        {
            Buffer.BlockCopy(bgra, 0, result, 0, bgra.Length);
            return result;
        }

        Span<byte> lookup = stackalloc byte[256];
        for (var value = 0; value < 256; value++)
        {
            lookup[value] = (byte)Math.Clamp((value - lo) * 255 / (hi - lo), 0, 255);
        }

        for (var i = 0; i < pixels; i++)
        {
            var offset = i * BgraImage.BytesPerPixel;
            result[offset] = lookup[bgra[offset]];
            result[offset + 1] = lookup[bgra[offset + 1]];
            result[offset + 2] = lookup[bgra[offset + 2]];
            result[offset + 3] = 255;
        }

        return result;
    }
}
