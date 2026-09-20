namespace TranslationApp.Core.Capture;

/// <summary>
/// 三种渲染模式与降级结果（FR-027 / 14.3.5）。
/// </summary>
public enum PinOverlayMode
{
    /// <summary>A. 原位替换（默认）：逐块覆盖原文字区域并绘制该块译文。</summary>
    InPlace = 0,

    /// <summary>B. 整块排版：在裁剪图上取一个统一底板一次覆盖，内部统一排版全部译文。</summary>
    BlockOverlay = 1,

    /// <summary>C. 下方译文面板：**不改动图片**，钉图窗口下方多一段译文面板。</summary>
    SidePanel = 2,

    /// <summary>不覆盖、无面板（翻译失败 / 语言相同）：只显示原图 + 工具条上的一句话说明。</summary>
    ImageOnly = 3,
}

/// <summary>
/// 文本测量回调（便于单测注入假测量器，与 <c>EngineComparison</c> 的回调风格一致）：
/// 给定文本、字号与最大行宽，返回排版后的宽高（px）。
/// </summary>
public delegate (double Width, double Height) TextMeasure(string text, double fontSizePx, double maxWidthPx);

/// <summary>排版输入。</summary>
/// <param name="Blocks">版面块（<see cref="OcrBlockGrouping.Group"/> 的输出）。</param>
/// <param name="Translations">逐块译文（与 <paramref name="Blocks"/> 同序；null/空 = 该块无可用译文）。</param>
/// <param name="ImageWidth">裁剪图像素宽。</param>
/// <param name="ImageHeight">裁剪图像素高。</param>
/// <param name="DpiScale">钉图所在屏的缩放（字号下限按 DIP 折算，绝不能写死 9 px）。</param>
/// <param name="Backgrounds">逐块底色取样（同序；缺失时按失败处理 → 兜底色令牌）。</param>
/// <param name="InkArgbs">逐块文字墨色取样（FR-048，同序；null 项 = 采样失败 → 界面按底色深浅兜底黑/白）。</param>
/// <param name="MergedTranslation">合并成一次请求时的整段译文（非空则整块排版直接用它）。</param>
/// <param name="ForcedSidePanelReason">链路已判定的降级原因（倾斜 / 竖排 / 语言相同等）。</param>
/// <param name="SegmentCountMismatch">合并请求切分出的段数与原文段数不一致（文档降级条件③）。</param>
/// <param name="TranslationFailed">翻译全部失败（仍然钉图，只显示原图 + 说明 + 重试）。</param>
public sealed record OverlayRequest(
    IReadOnlyList<OcrBlock> Blocks,
    IReadOnlyList<string?> Translations,
    int ImageWidth,
    int ImageHeight,
    double DpiScale,
    IReadOnlyList<BackgroundSample> Backgrounds,
    string? MergedTranslation = null,
    string? ForcedSidePanelReason = null,
    bool SegmentCountMismatch = false,
    bool TranslationFailed = false,
    IReadOnlyList<uint?>? InkArgbs = null);

/// <summary>排版结果：直接喂给 <see cref="PinContent"/>（Core 内不产生任何颜色常量）。</summary>
/// <param name="Mode">实际采用（或降级后）的渲染模式。</param>
/// <param name="Blocks">覆盖块（图像像素坐标）。</param>
/// <param name="PanelText">下方译文面板的文本（仅 SidePanel 非空）。</param>
/// <param name="PanelHeightDip">面板高度（DIP，随结果一并算出，窗口据此把物理高度加上它）。</param>
/// <param name="StatusMessage">工具条上的一句话说明（降级原因必须说清楚，不静默降级）。</param>
/// <param name="TranslatedText">可复制的完整译文。</param>
public sealed record OverlayPlan(
    PinOverlayMode Mode,
    IReadOnlyList<PinOverlayBlock> Blocks,
    string? PanelText = null,
    double PanelHeightDip = 0,
    string? StatusMessage = null,
    string? TranslatedText = null)
{
    /// <summary>
    /// 判定时算出的面积比（Σ译文所需面积 / Σ原文段框面积；无块或无法测量时为 null）。
    /// **只用于诊断日志与真机复验**（把「为什么降级」量化下来），不参与渲染。
    /// </summary>
    public double? AreaRatio { get; init; }
}

/// <summary>
/// 排版决策（FR-027 / 14.3.5，纯逻辑 + 测量回调，可单测）：
/// 模式 A 逐块给出「覆盖框 + 译文 + 字号」，字号从上限按 6% 步进下探到能放进段框为止，
/// 都放不下时允许向下扩展（上限 +0.6 段框高），仍放不下即降级 B；模式 C 完全不改图片。
///
/// 坐标系：这里是**图像像素**（与 <see cref="PinOverlayBlock"/> 一致），窗口侧再乘统一系数 k 换算到 DIP。
/// </summary>
public static class OverlayLayout
{
    /// <summary>字号上限 = min(0.92 × 段框高, 原文估算字号 × 1.2)。</summary>
    public const double MaxFontHeightRatio = 0.92;

    /// <summary>字号上限相对原文估算字号的放大倍数（原文 + 20%）。</summary>
    public const double FontUpperGrowth = 1.2;

    /// <summary>字号下探步进（比例，≈6%）。</summary>
    public const double FontStepRatio = 0.94;

    /// <summary>最小可读字号的 DIP 值（文档 14.3.5：11 DIP）。</summary>
    public const double MinFontDip = 11.0;

    /// <summary>最小字号的绝对下限（px）。</summary>
    public const double MinFontPxFloor = 9.0;

    /// <summary>覆盖块内边距（图像像素；每侧）。窗口侧按 k 换算后渲染，取值必须与这里一致。</summary>
    public const double PaddingPx = 4.0;

    /// <summary>向下扩展的可用净空扣除该值（文档 14.3.5：距下一个段框顶 − 6 px）。</summary>
    public const double ExpandGapPx = 6.0;

    /// <summary>向下扩展上限（× 段框高）。</summary>
    public const double ExpandRatioLimit = 0.6;

    /// <summary>整块排版的触发阈值：Σ译文所需面积 / Σ原文段框面积 &gt; 1.6。</summary>
    public const double AreaRatioLimit = 1.6;

    /// <summary>裁剪图高度小于该值时无法原位替换（文档 14.3.5 条件⑤）。</summary>
    public const int MinImageHeightPx = 60;

    /// <summary>面板字号的 DIP 值（界面字阶，非内容字号）。</summary>
    public const double PanelFontDip = 14.0;

    /// <summary>面板内边距（DIP）。</summary>
    public const double PanelPaddingDip = 12.0;

    /// <summary>面板最小高度（DIP）。</summary>
    public const double PanelMinDip = 72.0;

    /// <summary>面板高度上限（占图片 DIP 高度的比例）。</summary>
    public const double PanelMaxHeightRatio = 0.5;

    public const string SidePanelTilted = "文字倾斜，已改为下方译文面板";

    public const string SidePanelVertical = "检测到竖排文本，已改为下方译文面板";

    public const string SidePanelBusy = "背景较复杂，已改为下方译文面板";

    public const string SidePanelTooManyBlocks = "段落过多，已改为下方译文面板";

    public const string SidePanelTooSmall = "选区过小，已改为下方译文面板";

    public const string SidePanelNoBlocks = "未识别到文字位置，已改为下方译文面板";

    public const string BlockOverlayTooMuchText = "译文较长，已改用整块排版";

    public const string BlockOverlayNoRoom = "部分段落放不下，已改用整块排版";

    public const string BlockOverlaySegments = "段落切分与原文不一致，已改用整块排版";

    /// <summary>模式 C 里译文比面板还长、已铺满整张图片时的说明。</summary>
    public const string BlockOverlayFullImage = "译文很长，已铺满整张图片";

    /// <summary>字号下限：11 DIP 对应的物理像素，且不低于 9 px（按 DPI 折算，绝不写死）。</summary>
    public static double MinFontPx(double dpiScale) =>
        Math.Max(Math.Round(MinFontDip * (dpiScale > 0 ? dpiScale : 1.0), MidpointRounding.AwayFromZero), MinFontPxFloor);

    /// <summary>
    /// 排版决策 + 构建覆盖块。任何降级都在 <see cref="OverlayPlan.StatusMessage"/> 里说明原因。
    /// </summary>
    public static OverlayPlan Build(OverlayRequest request, TextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blocks = request.Blocks ?? [];
        var translations = ResolveTranslations(blocks, request.Translations);
        var fullText = request.MergedTranslation
            ?? OcrBlockGrouping.JoinLines(translations.Where(text => !string.IsNullOrEmpty(text)).Select(text => text!));

        // ---- 链路已判定的降级（倾斜 / 竖排 / 语言相同） ----
        if (request.ForcedSidePanelReason is { Length: > 0 } forced)
        {
            return SidePanel(request, forced, fullText);
        }

        if (request.TranslationFailed)
        {
            return ImageOnly(fullText);
        }

        // ---- 文档 14.3.5 模式 C 的硬条件 ----
        if (request.ImageHeight < MinImageHeightPx)
        {
            return SidePanel(request, SidePanelTooSmall, fullText);
        }

        if (blocks.Count == 0)
        {
            return SidePanel(request, SidePanelNoBlocks, fullText);
        }

        if (blocks.Count > OcrBlockGrouping.MaxInPlaceBlocks)
        {
            return SidePanel(request, SidePanelTooManyBlocks, fullText);
        }

        if (BackgroundSampler.IsMostlyBusy(request.Backgrounds))
        {
            return SidePanel(request, SidePanelBusy, fullText);
        }

        if (request.SegmentCountMismatch)
        {
            return BlockOverlay(request, BlockOverlaySegments, fullText, measure);
        }

        // ---- 文档 14.3.5 模式 B 条件①：Σ译文所需面积 / Σ原文段框面积 > 1.6 ----
        var areaRatio = AreaRatio(blocks, translations, measure);
        if (areaRatio > AreaRatioLimit)
        {
            return BlockOverlay(request, BlockOverlayTooMuchText, fullText, measure) with { AreaRatio = areaRatio };
        }

        // ---- 模式 A：逐块自适应字号；任一块放不下即降级 B ----
        var placements = new List<PinOverlayBlock>(blocks.Count);
        foreach (var index in Enumerable.Range(0, blocks.Count))
        {
            var block = blocks[index];
            var text = translations[index] ?? string.Empty;
            var fit = FitBlock(block, text, blocks, request, measure);
            if (fit is null)
            {
                return BlockOverlay(request, BlockOverlayNoRoom, fullText, measure) with { AreaRatio = areaRatio };
            }

            placements.Add(fit.Value);
        }

        return new OverlayPlan(
            PinOverlayMode.InPlace, placements, StatusMessage: null, TranslatedText: fullText)
        {
            AreaRatio = areaRatio,
        };
    }

    /// <summary>
    /// 不折行的测量宽度（<see cref="TextMeasure"/> 的 <c>maxWidthPx</c> 传它即得到「自然宽度 + 单行行高」）。
    /// 取 10 万像素：任何屏幕都远小于它，因此等价于不折行，又不必给测量回调塞 <c>double.MaxValue</c>。
    /// </summary>
    public const double NoWrapWidthPx = 100000.0;

    /// <summary>
    /// 文档 14.3.5 模式 B 条件①的面积比：<c>Σ译文所需面积 / Σ原文段框面积</c>。
    ///
    /// 分子**一律实测**（同一个 <see cref="TextMeasure"/>，生产环境即 WPF <c>FormattedText</c>），
    /// 不做任何「字符数 × 单字宽」估算——中文比拉丁字符宽这点由字体引擎自己算准；
    /// 分母仍按文档取段框面积（<see cref="OcrBlock.Rect"/> 的并集面积）。
    ///
    /// 关键是**两侧必须同为「墨迹高度」量纲**：<c>FormattedText.Height</c> 含行距（一个字号下约 1.33×），
    /// 而段框高是墨高（约 0.75× 字号），直接把实测高度当分子会把比值系统性放大 ≈1.78 倍。
    /// 那样阈值 1.6 形同虚设——只要译文折行铺满段框宽度就会触发（哪怕译文与原文一样短），
    /// 实测已复现：960×170、44 字符、5 行的短选区被降级成「整块排版」，译文挤成一个小面板、
    /// 逐段原位替换（模式 A）完全用不上。
    /// 因此此处把实测总高按「实测单行高」折算成行数，再乘原文每行墨高得到「所需高度」，
    /// 与段框面积同量纲后比值才真正表达「译文比原文长多少」。
    /// </summary>
    public static double AreaRatio(
        IReadOnlyList<OcrBlock> blocks, IReadOnlyList<string?> translations, TextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(measure);

        var needed = 0.0;
        var source = 0.0;
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            source += block.Rect.Area;

            var text = index < translations.Count ? translations[index] : null;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var font = block.EstimatedFontPx;
            var width = Math.Max(1, block.Rect.Width);
            var wrapped = measure(text!, font, width);              // 按段框宽折行后的所需宽高
            var single = measure(text!, font, NoWrapWidthPx);        // 自然宽度 + 单行行高
            var lines = single.Height > 0
                ? Math.Max(1, Math.Round(wrapped.Height / single.Height, MidpointRounding.AwayFromZero))
                : 1;
            var inkLine = block.Rect.Height / Math.Max(1, block.LineIndexes.Count); // 原文每行墨高

            needed += Math.Max(0, Math.Min(single.Width, width) * lines * inkLine);
        }

        return source > 0 ? needed / source : 0;
    }

    /// <summary>
    /// 单个块的字号自适应：从上限按 6% 下探找第一个放得下的字号；都放不下时允许向下扩展段框
    /// （净空 = 到下一个段框顶或图片底 − 6 px，上限 +0.6 × 段框高）；仍放不下返回 null（→ 降级 B）。
    /// 字号低于下限时**不画不可读的小字**，直接判定为放不下（宁可降级也不给用户看不清的结果）。
    /// </summary>
    private static PinOverlayBlock? FitBlock(
        OcrBlock block,
        string text,
        IReadOnlyList<OcrBlock> blocks,
        OverlayRequest request,
        TextMeasure measure)
    {
        var cover = block.CoverRect;
        if (cover.IsEmpty)
        {
            return null;
        }

        var minFont = MinFontPx(request.DpiScale);
        var upper = Math.Min(MaxFontHeightRatio * cover.Height, block.EstimatedFontPx * FontUpperGrowth);
        var expandLimit = Math.Min(ExpandRatioLimit * cover.Height, Math.Max(0, Clearance(block, blocks, request.ImageHeight)));
        var argb = BackgroundArgb(request.Backgrounds, block.Index);
        var textArgb = InkArgb(request.InkArgbs, block.Index);

        // 第一轮：不扩展
        if (TryFit(text, cover.Height, cover.Width, upper, minFont, measure, out var font, out _, out var wrapped))
        {
            return new PinOverlayBlock(cover.ToPixelRect(), text, argb, font, wrapped, textArgb);
        }

        // 第二轮：向下扩展（扩展量按需要的实际高度取，仍受净空与上限约束）
        if (expandLimit > 0
            && TryFit(text, cover.Height + expandLimit, cover.Width, upper, minFont, measure, out font, out var used, out wrapped)
            && used > cover.Height)
        {
            var expanded = new OcrRect(cover.Left, cover.Top, cover.Right, cover.Bottom + (used - cover.Height));
            return new PinOverlayBlock(expanded.ToPixelRect(), text, argb, font, wrapped, textArgb);
        }

        return null;
    }

    /// <summary>从字号上限向下找第一个「宽高都放得下」的字号；返回 null 表示到下限都放不下。</summary>
    /// <param name="wrapped">
    /// 该字号下测量时**是否已经折行**。测量（<see cref="TextMeasure"/>）按可用宽度折行、只校验总高，
    /// 因此它接受多行结果；而界面渲染模式 A 的块时若用不换行，译文会被 <c>TextTrimming</c> 截成
    /// 「Marketplace G...」这种半截文本——字号稍小就能多行放下，却看不到完整译文。
    /// 所以「测量是否折行」必须一路传到 <see cref="PinOverlayBlock.Wrap"/>，测量与渲染才一致。
    /// </param>
    private static bool TryFit(
        string text,
        double availableHeight,
        double availableWidth,
        double upper,
        double minFont,
        TextMeasure measure,
        out double fontPx,
        out double usedHeight,
        out bool wrapped)
    {
        fontPx = 0;
        usedHeight = 0;
        wrapped = false;
        if (upper < minFont || availableHeight <= 0 || availableWidth <= 0)
        {
            return false;
        }

        var maxWidth = Math.Max(1, availableWidth - (2 * PaddingPx));
        var maxHeight = Math.Max(1, availableHeight - (2 * PaddingPx));
        var chosen = 0.0;
        var used = 0.0;
        var wraps = false;

        for (var font = upper; font >= minFont && chosen <= 0; font *= FontStepRatio)
        {
            if (TryMeasure(text, font, maxWidth, maxHeight, measure, out used, out wraps))
            {
                chosen = font;
            }
        }

        // 步进是 ×0.94，可能整段跳过 [下限, 下限 ÷ 0.94) 这个依然合法的区间（例如下限 11、上一次已试到 11.6），
        // 所以下限字号本身必须再试一次——否则「差一点点就能放下」的段会被无谓地降级
        if (chosen <= 0 && minFont < upper
            && TryMeasure(text, minFont, maxWidth, maxHeight, measure, out used, out wraps))
        {
            chosen = minFont;
        }

        fontPx = chosen;
        usedHeight = chosen > 0 ? used : 0;
        wrapped = chosen > 0 && wraps;
        return chosen > 0;
    }

    /// <summary>该字号下能否放进 <paramref name="maxWidth"/> × <paramref name="maxHeight"/>（实测）；
    /// 放得下则一并给出实际占用高度与是否需要换行。</summary>
    private static bool TryMeasure(
        string text,
        double font,
        double maxWidth,
        double maxHeight,
        TextMeasure measure,
        out double usedHeight,
        out bool wrapped)
    {
        var size = measure(text ?? string.Empty, font, maxWidth);
        usedHeight = 0;
        wrapped = false;
        if (size.Height > maxHeight || size.Width > maxWidth + 0.5)
        {
            return false;
        }

        usedHeight = size.Height + (2 * PaddingPx);
        wrapped = measure(text ?? string.Empty, font, NoWrapWidthPx).Width > maxWidth + 0.5;
        return true;
    }

    /// <summary>
    /// 向下扩展的可用净空：到下一个块覆盖框顶（没有就到图片底）的距离；
    /// **只有下方确实还有别的段时**才再扣 <see cref="ExpandGapPx"/>。
    /// 那个 6 px 的作用是不与下一段的文字贴边，而图片底边下面没有任何内容可贴：
    /// 照扣会让选区最下面那一段白白少掉 6 px 净空（真机复现：960×170 选区的最后一行因此放不下、
    /// 整张图被降级成「整块排版」）。
    /// </summary>
    private static double Clearance(OcrBlock block, IReadOnlyList<OcrBlock> blocks, int imageHeight)
    {
        var bottom = block.CoverRect.Bottom;
        var limit = (double)imageHeight;
        var boundedByBlock = false;
        foreach (var other in blocks)
        {
            if (ReferenceEquals(other, block) || other.Index == block.Index)
            {
                continue;
            }

            var top = other.CoverRect.Top;
            if (top >= bottom - 0.5 && top < limit)
            {
                limit = top;
                boundedByBlock = true;
            }
        }

        var clearance = limit - bottom;
        return boundedByBlock ? clearance - ExpandGapPx : clearance;
    }

    /// <summary>模式 B：统一底板 = 全体覆盖框的外接矩形（放不下时退化为整张图片）。</summary>
    private static OverlayPlan BlockOverlay(
        OverlayRequest request, string reason, string fullText, TextMeasure measure)
    {
        var blocks = request.Blocks;
        var panel = OcrRect.Union(blocks.Select(block => block.CoverRect)).ClampTo(request.ImageWidth, request.ImageHeight);
        var argb = BackgroundSampler.DominantArgb(request.Backgrounds);
        var minFont = MinFontPx(request.DpiScale);
        var text = string.IsNullOrEmpty(request.MergedTranslation) ? fullText : request.MergedTranslation;

        var border = reason;
        var sourceFont = blocks.Count > 0 ? blocks.Max(block => block.EstimatedFontPx) : minFont;
        var upper = Math.Max(minFont, Math.Min(MaxFontHeightRatio * panel.Height, sourceFont * FontUpperGrowth));
        if (!TryFit(text, panel.Height, panel.Width, upper, minFont, measure, out var font, out _, out _))
        {
            // 底板放不下：铺满整张图片再试一次；仍放不下就保底用下限字号（交给界面换行显示，绝不丢文字）
            panel = new OcrRect(0, 0, request.ImageWidth, request.ImageHeight);
            upper = Math.Max(minFont, Math.Min(MaxFontHeightRatio * panel.Height, sourceFont * FontUpperGrowth));
            if (!TryFit(text, panel.Height, panel.Width, upper, minFont, measure, out font, out _, out _))
            {
                font = minFont;
                border = BlockOverlayFullImage;
            }
        }

        var block = new PinOverlayBlock(panel.ToPixelRect(), text, argb, font, Wrap: true,
            TextArgb: DominantInk(request.InkArgbs));
        return new OverlayPlan(
            PinOverlayMode.BlockOverlay, [block], StatusMessage: border, TranslatedText: fullText);
    }

    /// <summary>模式 C：不改动图片，只给下方译文面板（面板高度按字数与图片尺寸估算，窗口据此加高）。</summary>
    private static OverlayPlan SidePanel(OverlayRequest request, string reason, string fullText) =>
        new(
            PinOverlayMode.SidePanel,
            [],
            PanelText: fullText,
            PanelHeightDip: EstimatePanelDipHeight(fullText, request.ImageWidth, request.ImageHeight, request.DpiScale),
            StatusMessage: reason,
            TranslatedText: fullText);

    private static OverlayPlan ImageOnly(string fullText) =>
        new(PinOverlayMode.ImageOnly, [], TranslatedText: fullText, StatusMessage: null);

    /// <summary>面板高度估算（DIP）：按面板宽度与字数折行，钳制在 [72 DIP, 图片 DIP 高 × 0.5]。</summary>
    public static double EstimatePanelDipHeight(string? text, int imageWidth, int imageHeight, double dpiScale)
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        var imageDipHeight = Math.Max(1, imageHeight / scale);
        var max = Math.Max(PanelMinDip, imageDipHeight * PanelMaxHeightRatio);
        if (string.IsNullOrEmpty(text))
        {
            return PanelMinDip;
        }

        var widthDip = Math.Max(120, (imageWidth / scale) - (2 * PanelPaddingDip));
        var unitsPerLine = Math.Max(1.0, widthDip / PanelFontDip);
        var units = 0.0;
        foreach (var ch in text)
        {
            units += ch == '\n' ? unitsPerLine - (units % unitsPerLine) : OcrBlockGrouping.IsCjk(ch) ? 1.0 : 0.55;
        }

        var lines = Math.Max(1, (int)Math.Ceiling(units / unitsPerLine));
        return Math.Clamp((lines * PanelFontDip * 1.45) + (2 * PanelPaddingDip), PanelMinDip, max);
    }

    /// <summary>该块的覆盖色：优先取底色取样结果，缺失即 null（界面回退 <c>Brush.Overlay.CoverFallback</c>）。</summary>
    private static uint? BackgroundArgb(IReadOnlyList<BackgroundSample>? samples, int index) =>
        samples is not null && index >= 0 && index < samples.Count ? samples[index].Argb : null;

    /// <summary>该块的文字墨色（FR-048）：越界或缺采样列表即 null（界面按底色深浅兜底黑/白）。</summary>
    private static uint? InkArgb(IReadOnlyList<uint?>? inks, int index) =>
        inks is not null && index >= 0 && index < inks.Count ? inks[index] : null;

    /// <summary>模式 B 统一底板的墨色：取出现次数最多的采样值（多样本投票，与 <see cref="BackgroundSampler.DominantArgb"/> 同一思路）。</summary>
    private static uint? DominantInk(IReadOnlyList<uint?>? inks)
    {
        if (inks is null || inks.Count == 0)
        {
            return null;
        }

        var counts = new Dictionary<uint, int>();
        foreach (var ink in inks)
        {
            if (ink is { } value)
            {
                counts[value] = counts.TryGetValue(value, out var seen) ? seen + 1 : 1;
            }
        }

        return counts.Count == 0 ? null : counts.MaxBy(pair => pair.Value).Key;
    }

    private static IReadOnlyList<string?> ResolveTranslations(
        IReadOnlyList<OcrBlock> blocks, IReadOnlyList<string?>? translations)
    {
        var result = new string?[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            result[i] = translations is not null && i < translations.Count ? translations[i] : null;
        }

        return result;
    }
}
