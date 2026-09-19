namespace TranslationApp.Core.Capture;

/// <summary>
/// 钉图当前显示的层（FR-027 / 14.3.6 原文/译文切换）：
/// <see cref="Original"/> = 覆盖层整体不可见（看到的就是原始截图），
/// <see cref="Translation"/> = 覆盖层可见（原位替换或整块替换后的译文）。
/// **切换状态只属于该张钉图**（存在窗口字段里），不写回设置、不跨张共享。
/// </summary>
public enum PinTextLayer
{
    /// <summary>原文：看未被覆盖的原始图片。</summary>
    Original = 0,

    /// <summary>译文：看覆盖块 + 译文。</summary>
    Translation = 1,
}

/// <summary>
/// 覆盖层的一个块（<b>图像像素坐标</b>，相对裁剪图左上角）：批 4c 把每个 OCR 段变成一个块。
/// 颜色留空（<c>null</c>）时由界面按固定色令牌兜底（`Brush.Overlay.CoverFallback` / `TextOnLight|TextOnDark`），
/// 因此 Core 里没有任何颜色常量，也不写死颜色；4c 取样出底色后把 ARGB 放进 <see cref="CoverArgb"/> 即可。
/// </summary>
/// <param name="FontSizePx">内容字号（图像像素）；≤0 时由界面按块高推算。</param>
/// <param name="Wrap">是否需要换行（模式 B 整块排版的多行译文；模式 A 由排版阶段保证单行放得下）。</param>
public readonly record struct PinOverlayBlock(
    PixelRect Rect, string Text, uint? CoverArgb = null, double FontSizePx = 0, bool Wrap = false);

/// <summary>
/// 钉图的内容数据（批 4c 传入真实 OCR 段落与译文；**只传数据，不改窗口结构**）。
/// </summary>
/// <param name="SourceText">识别出的原文（「在小窗中打开」与复制原文用）。</param>
/// <param name="TranslatedText">整段译文（「复制译文」复制的就是它；为空即禁用复制并说明原因）。</param>
/// <param name="StatusMessage">工具条上的一句话说明（降级原因 / 引擎错误 / 语言相同等，14.3 要求不静默降级）。</param>
public sealed record PinContent(
    IReadOnlyList<PinOverlayBlock> Blocks,
    string? SourceText = null,
    string? TranslatedText = null,
    string? StatusMessage = null)
{
    /// <summary>占位文字（批 4b 的演示用；批 4c 起真实流程不再使用）。</summary>
    public const string PlaceholderText = "示例译文（占位）";

    /// <summary>翻译失败，工具条上出现「重试」按钮（14.3.2 / AC 12：一次网络抖动不该丢掉用户的截图）。</summary>
    public bool CanRetry { get; init; }

    /// <summary>
    /// 处于「识别语言与目标语言相同」的跳过态、可强制翻译（v1.2 修复批 ③-C）：
    /// 工具条出现带文字的「翻译」按钮，点击后把源语言置回自动检测重新翻译并原位显示。
    /// </summary>
    public bool CanForceTranslate { get; init; }

    /// <summary>下方译文面板的文本（仅模式 C <c>SidePanel</c> 非空；面板属于界面，走主题令牌）。</summary>
    public string? PanelText { get; init; }

    /// <summary>面板高度（DIP）；&gt; 0 时钉图窗口的物理高度 = 图像像素 × 缩放 + 面板 DIP × 屏缩放。</summary>
    public double PanelHeightDip { get; init; }

    /// <summary>没有任何覆盖内容（钉图只剩图片，切换按钮将被禁用并说明原因）。</summary>
    public static PinContent Empty { get; } = new([]);

    /// <summary>是否有可复制的译文。</summary>
    public bool HasTranslation => !string.IsNullOrWhiteSpace(TranslatedText);

    /// <summary>是否有可切换的覆盖内容。</summary>
    public bool HasOverlay => Blocks.Count > 0;

    /// <summary>是否有下方译文面板（模式 C）。</summary>
    public bool HasPanel => !string.IsNullOrWhiteSpace(PanelText);

    /// <summary>是否有原文可供「在小窗中打开」。</summary>
    public bool HasSource => !string.IsNullOrWhiteSpace(SourceText);

    /// <summary>
    /// 占位内容（批 4b）：在图像中部横亘一条覆盖块 + 一行示例文字。
    /// 真实内容由 <see cref="OverlayLayout"/> 产出（模式 A/B）或走面板（模式 C）。
    /// </summary>
    public static PinContent Placeholder(int imageWidth, int imageHeight)
    {
        var w = Math.Max(1, imageWidth);
        var h = Math.Max(1, imageHeight);

        var left = Math.Clamp((int)Math.Round(w * 0.06), 0, Math.Max(0, w - 1));
        var top = Math.Clamp((int)Math.Round(h * 0.34), 0, Math.Max(0, h - 1));
        var blockWidth = Math.Max(1, Math.Min(w - left, (int)Math.Round(w * 0.88)));
        var blockHeight = Math.Max(1, Math.Min(h - top, (int)Math.Round(h * 0.32)));

        return new PinContent([new PinOverlayBlock(new PixelRect(left, top, blockWidth, blockHeight), PlaceholderText)]);
    }
}

/// <summary>
/// 原文/译文切换与覆盖层可见性的纯逻辑（FR-027 / 14.3.6）：默认层、翻转、不透明度映射与
/// 「深底配浅字 / 浅底配深字」的令牌选择都在这里，因此切换规则可在无窗口环境下穷举单测。
///
/// 关键实现取舍（14.3.6 明确）：切换**不准备两张图**，只把覆盖层整体 `Opacity` 在 0 ↔ 1 之间交叉淡入，
/// 因此译文层的不透明度变化不改变窗口尺寸 → 无布局抖动。
/// </summary>
public static class PinOverlayRules
{
    /// <summary>浅色底的相对亮度阈值（≥ 该值视为浅色底，配深色文字）。</summary>
    public const double LightBackgroundThreshold = 0.5;

    /// <summary>
    /// 初始层：有译文且设置要求优先显示译文时看译文，否则看原文
    /// （14.3.8：`pin` 模式下 <c>OcrInPlaceReplace</c> 关闭时钉图先显示原文，点工具条才翻译/显示译文）。
    /// </summary>
    public static PinTextLayer Default(bool hasTranslation, bool preferTranslation) =>
        hasTranslation && preferTranslation ? PinTextLayer.Translation : PinTextLayer.Original;

    /// <summary>翻转当前层（双击 / 空格 / T / 工具条按钮共用同一处翻转逻辑）。</summary>
    public static PinTextLayer Toggle(PinTextLayer layer) =>
        layer == PinTextLayer.Original ? PinTextLayer.Translation : PinTextLayer.Original;

    /// <summary>覆盖层的目标不透明度：原文 0、译文 1（交叉淡入的两端）。</summary>
    public static double TargetOpacity(PinTextLayer layer) =>
        layer == PinTextLayer.Translation ? 1.0 : 0.0;

    /// <summary>该层是否应显示覆盖层（= 工具条切换按钮的激活态）。</summary>
    public static bool IsOverlayVisible(PinTextLayer layer) => layer == PinTextLayer.Translation;

    /// <summary>
    /// sRGB 相对亮度（忽略 alpha：覆盖块的 alpha 由取样决定，与「配深字还是浅字」无关）。
    /// 用于按文档「浅底图上的替换文字 / 深底图上的替换文字」二选一。
    /// </summary>
    public static double RelativeLuminance(uint argb)
    {
        var r = Linear((byte)((argb >> 16) & 0xFF));
        var g = Linear((byte)((argb >> 8) & 0xFF));
        var b = Linear((byte)(argb & 0xFF));
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>该底色是否算浅色底。</summary>
    public static bool IsLightBackground(uint argb) => RelativeLuminance(argb) >= LightBackgroundThreshold;

    /// <summary>
    /// 覆盖块文字该用哪枚令牌：底色取自图片内容，故**不跟主题**（14.3.9）。
    /// 底色未知（取样失败 → 界面用 `CoverFallback` 中性灰 #6F7780）时取深色底那枚，灰底上浅字可读性更好。
    /// </summary>
    public static string TextColorToken(uint? coverArgb) =>
        coverArgb is { } argb && IsLightBackground(argb)
            ? "Brush.Overlay.TextOnLight"
            : "Brush.Overlay.TextOnDark";

    private static double Linear(byte value)
    {
        var s = value / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
