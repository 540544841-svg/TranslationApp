namespace TranslationApp.Core.Capture;

/// <summary>
/// OCR 边界框（**浮点**图像像素坐标，原点 = 传入 <c>OcrEngine.RecognizeAsync</c> 的那张位图左上角）。
///
/// 实测依据（本机 1920×1080、100% 缩放、zh-Hans-CN 引擎，852 词）：
/// 裁剪区 (650,130,470×350) 再识别的 133 个词框**全部**与「全屏词框 − (650,130)」在 2 px 内吻合，
/// 且裁图词框范围 Left 0~441 / Top 4~332（≪ 全屏坐标）→ 坐标系归属确定无疑：**1:1 属于传入位图**。
/// 唯一需要的换算是识别前做过等比缩小时乘 <c>1/Ratio</c>（实测 ratio=0.8 缩图 ×1.25 还原后同词偏差
/// Left p50=0.38 px / p90=1 px、Top p50=0.25 px → 换算成立）。
/// </summary>
public readonly record struct OcrRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public double Area => IsEmpty ? 0 : Width * Height;

    /// <summary>是否包含竖排/堆叠特征：高 &gt; 宽 × 2.5。</summary>
    public bool IsTall => Width > 0 && Height > Width * 2.5;

    /// <summary>并集（任一为空则跳过；全空返回空矩形）。行框统一由词框并集算出——`OcrLine` 没有 `BoundingRect`。</summary>
    public static OcrRect Union(IEnumerable<OcrRect> rects)
    {
        var any = false;
        double left = 0, top = 0, right = 0, bottom = 0;
        foreach (var rect in rects)
        {
            if (rect.IsEmpty)
            {
                continue;
            }

            if (!any)
            {
                (left, top, right, bottom) = (rect.Left, rect.Top, rect.Right, rect.Bottom);
                any = true;
                continue;
            }

            left = Math.Min(left, rect.Left);
            top = Math.Min(top, rect.Top);
            right = Math.Max(right, rect.Right);
            bottom = Math.Max(bottom, rect.Bottom);
        }

        return any ? new OcrRect(left, top, right, bottom) : default;
    }

    /// <summary>等比缩放（识别前缩小过时按 <c>1/ratio</c> 还原回原裁剪图像素）。</summary>
    public OcrRect ScaleBy(double factor) => new(Left * factor, Top * factor, Right * factor, Bottom * factor);

    /// <summary>按系数扩边（覆盖原文字用，见 <see cref="OcrLayoutRules.CoverVerticalRatio"/>）。</summary>
    public OcrRect Expand(double vertical, double horizontal) =>
        new(Left - horizontal, Top - vertical, Right + horizontal, Bottom + vertical);

    /// <summary>水平重叠率 = (min(R1,R2) − max(L1,L2)) / min(W1,W2)；用于「上下两行是否同段」的判定。</summary>
    public double HorizontalOverlapRatio(OcrRect other)
    {
        var minWidth = Math.Min(Width, other.Width);
        if (minWidth <= 0)
        {
            return 0;
        }

        var overlap = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        return Math.Max(0, overlap) / minWidth;
    }

    /// <summary>钳制到图像范围内（保持 x/y 不小于 0，右/下不超出图像）。</summary>
    public OcrRect ClampTo(double width, double height)
    {
        var left = Math.Clamp(Left, 0, Math.Max(0, width));
        var top = Math.Clamp(Top, 0, Math.Max(0, height));
        var right = Math.Clamp(Right, left, Math.Max(left, width));
        var bottom = Math.Clamp(Bottom, top, Math.Max(top, height));
        return new OcrRect(left, top, right, bottom);
    }

    /// <summary>四舍五入为整数像素矩形（钉图覆盖层用），宽高至少 1 px。</summary>
    public PixelRect ToPixelRect() => new(
        (int)Math.Round(Left, MidpointRounding.AwayFromZero),
        (int)Math.Round(Top, MidpointRounding.AwayFromZero),
        Math.Max(1, (int)Math.Round(Width, MidpointRounding.AwayFromZero)),
        Math.Max(1, (int)Math.Round(Height, MidpointRounding.AwayFromZero)));
}

/// <summary>一个 OCR 词的文本与边界框（字形的**紧框**：不含行高、不含两侧留白、不含词间空格）。</summary>
public sealed record OcrWordBox(string Text, OcrRect Rect);

/// <summary>
/// 一个 OCR 视觉行：`OcrLine` 只有 <c>Text</c> 与 <c>Words</c>，**没有** <c>BoundingRect</c>，
/// 因此行框必须由该行全部词框并集算出（文档 14.3.1 明确警告过这个坑）。
/// </summary>
public sealed record OcrLineBox(int Index, string Text, IReadOnlyList<OcrWordBox> Words, OcrRect Rect);

/// <summary>一次带布局的识别结果（坐标已还原到**原始裁剪图**像素）。</summary>
/// <param name="Lines">视觉行（按引擎给出的顺序）。</param>
/// <param name="TextAngle">引擎报告的顺时针角度；**实测正常横排也不是 null 而是 −0**（故判定必须看角度值，不能看是否为 null）。</param>
/// <param name="Width">裁剪图像素宽。</param>
/// <param name="Height">裁剪图像素高。</param>
/// <param name="RestoreRatio">识别前的等比缩小比例（1 = 未缩小）。</param>
/// <param name="LineHeight">行框高**中位数**，扩边系数与行间合并阈值都以它为基准。</param>
public sealed record OcrLayout(
    IReadOnlyList<OcrLineBox> Lines,
    double? TextAngle,
    int Width,
    int Height,
    double RestoreRatio,
    double LineHeight)
{
    /// <summary>是否没有可用的文字位置信息。</summary>
    public bool IsEmpty => Lines.Count == 0;
}

/// <summary>
/// OCR 布局的几何规则（纯函数，可单测）：坐标还原、旋转/竖排判定、行高中位数、覆盖扩边系数。
///
/// **扩边系数是实测校正过的，不是文档初值**（本机 852 词、逐词取「框边缘向外最近的墨迹」，单位是 ÷ 行框高）：
/// <list type="bullet">
/// <item>垂直残留 p50=0、p90=0~0.083、p99=0.10~0.11 → 文档初值 0.22 **充足且留有余量**，保留；</item>
/// <item>水平残留 p50=0.077、p90=0.167、p95=0.267 → 文档初值 0.10 **明显不足**（只覆盖约 6 成词），
/// 校正为 <see cref="CoverHorizontalRatio"/> = 0.20（≈ p90~p93）；</item>
/// <item>行间距 p50=0.67×行高、p90=1.92×行高 → 行内合并阈值 1.6×行高正好落在两个总体之间（实测支持该阈值）；</item>
/// <item>行内词间隙 p50=0.25×行高、p90=0.83×行高、max=3.83×行高 → 栏切分阈值 1.5×行高不会误切（实测支持）。</item>
/// </list>
/// </summary>
public static class OcrLayoutRules
{
    /// <summary>垂直每侧扩边系数（× 行框高）。实测 p99≈0.11（文档初值 0.22，保留）。</summary>
    public const double CoverVerticalRatio = 0.22;

    /// <summary>水平每侧扩边系数（× 行框高）。实测 p90≈0.167 / p95≈0.267（文档初值 0.10 不足，校正为 0.20）。</summary>
    public const double CoverHorizontalRatio = 0.20;

    /// <summary>倾斜判定阈值（度，含）：`|TextAngle| > 3` 即不做原位替换（文档 14.3.1 第 5 条）。</summary>
    public const double TiltDegrees = 3.0;

    /// <summary>竖排启发式：行框高宽比 &gt; 2.5 且该行词数 ≥ 2。</summary>
    public const double VerticalAspectRatio = 2.5;

    /// <summary>字形墨高 → 估算字号的换算（拉丁约 0.7、CJK 约 0.9，取折中；仅用于字号上限与面积比，不用于渲染）。</summary>
    public const double InkToFontRatio = 0.75;

    /// <summary>
    /// 是否倾斜到不能原位替换。**注意实测：正常横排的 `TextAngle` 是 −0 而不是 `null`**
    /// （本机 1920×1080 全屏与裁剪区两次识别均为 −0），所以只能按角度值判定，
    /// 「非 null 就降级」会把所有正常横排全部误降级。
    /// </summary>
    public static bool IsTilted(double? textAngle) =>
        textAngle is { } angle && Math.Abs(angle) > TiltDegrees;

    /// <summary>该行是否疑似竖排（文档启发式：高宽比 &gt; 2.5 且词数 ≥ 2）。</summary>
    public static bool IsVerticalLine(OcrLineBox line) =>
        line.Words.Count >= 2 && line.Rect.Height > line.Rect.Width * VerticalAspectRatio;

    /// <summary>整页是否检测到竖排文本（任一行满足即视为竖排，交由 SidePanel 降级）。</summary>
    public static bool HasVerticalText(IReadOnlyList<OcrLineBox> lines) =>
        lines.Any(IsVerticalLine);

    /// <summary>行框高中位数（扩边与行间合并的基准）；无有效行时退化为 1，绝不返回 0/NaN。</summary>
    public static double MedianLineHeight(IReadOnlyList<OcrLineBox> lines)
    {
        var heights = lines.Where(line => line.Rect.Height > 0).Select(line => line.Rect.Height).OrderBy(v => v).ToArray();
        if (heights.Length == 0)
        {
            return 1.0;
        }

        var mid = heights.Length / 2;
        return heights.Length % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2;
    }

    /// <summary>该行的估算字号（= 墨水高 / <see cref="InkToFontRatio"/>）。</summary>
    public static double EstimatedFontPx(double lineHeight) => Math.Max(1, lineHeight / InkToFontRatio);

    /// <summary>覆盖扩边：垂直 ±0.22×行高、水平 ±0.20×行高（实测校正值），再钳制进图像。</summary>
    public static OcrRect ExpandForCover(OcrRect rect, double lineHeight, int imageWidth, int imageHeight) =>
        rect.Expand(CoverVerticalRatio * lineHeight, CoverHorizontalRatio * lineHeight)
            .ClampTo(imageWidth, imageHeight);

    /// <summary>
    /// 把带布局的识别原始结果（坐标属于识别时那张位图）还原成本地坐标：
    /// <c>x = rect.X / ratio</c>（ratio = 识别前缩小比例；未缩小时为 1）。
    /// </summary>
    public static OcrLayout Create(
        IReadOnlyList<OcrLineBox> rawLines, double? textAngle, int width, int height, double restoreRatio)
    {
        var ratio = restoreRatio > 0 ? restoreRatio : 1.0;
        var lines = new List<OcrLineBox>();
        foreach (var line in rawLines ?? [])
        {
            var words = line.Words
                .Select(word => word with { Rect = word.Rect.ScaleBy(1.0 / ratio) })
                .ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            lines.Add(line with
            {
                Words = words,
                Rect = OcrRect.Union(words.Select(word => word.Rect)),
                Index = lines.Count,
            });
        }

        return new OcrLayout(
            lines, textAngle, Math.Max(0, width), Math.Max(0, height), ratio, MedianLineHeight(lines));
    }
}
