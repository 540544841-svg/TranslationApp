namespace TranslationApp.Core.Capture;

/// <summary>
/// 一个版面块（段落 / 多列中的一栏 / 表格单元 / 导航项）。
/// </summary>
/// <param name="Index">块序号（阅读顺序，用于与逐段译文对齐）。</param>
/// <param name="LineIndexes">成员行的序号（<see cref="OcrLineBox.Index"/>）。</param>
/// <param name="Rect">成员行框的**并集**（原文段框，不含扩边；用于面积比判定）。</param>
/// <param name="CoverRect">扩边并钳制进图像后的**覆盖框**（实际用于覆盖原文字的区域）。</param>
/// <param name="SourceText">段原文（成员行文本连接；中日韩不加空格，其余用单个空格）。</param>
public sealed record OcrBlock(
    int Index,
    IReadOnlyList<int> LineIndexes,
    OcrRect Rect,
    OcrRect CoverRect,
    string SourceText)
{
    /// <summary>段的估算字号（由段内行高中位数折算）。</summary>
    public double EstimatedFontPx => OcrLayoutRules.EstimatedFontPx(Rect.Height / Math.Max(1, LineIndexes.Count));
}

/// <summary>
/// 行/段重组（FR-027 / 14.3.2，纯函数放 Core，可单测）：
/// OCR 给的是**视觉行**而不是句子——逐行翻译既会被硬换行切断句子，译文行数也必然对不上原文行数，
/// 因此必须先按版面聚成块、**按块翻译**，再把整段译文放回该段覆盖的整块区域。
///
/// 两条实测支撑的阈值（本机 1920×1080、852 词）：
/// 行内词间隙 p50=0.25×行高、p90=0.83×行高 → 栏切分 1.5×行高不会误切；
/// 相邻行间距 p50=0.67×行高、p90=1.92×行高 → 段内合并 1.6×行高正好分开「同段换行」与「段间空档」。
/// </summary>
public static class OcrBlockGrouping
{
    /// <summary>同行内相邻词间隙 &gt; 该系数 × 行高中位数 → 视为不同栏/不同块，在此切开。</summary>
    public const double ColumnGapRatio = 1.5;

    /// <summary>上下两行水平重叠率下限（同段判定的条件①）。</summary>
    public const double MinHorizontalOverlap = 0.5;

    /// <summary>行间距上限（× 行高中位数），超过即视为新段（条件②）。</summary>
    public const double LineGapRatio = 1.6;

    /// <summary>句末标点：上一行以此结尾则不并入下一行（条件③）。</summary>
    public const string SentenceEnders = ".!?;:。！？；：…";

    /// <summary>段落数 ≤ 该值：逐段并发翻译（每段都有确定译文，天然对齐）。</summary>
    public const int MaxBlocksPerRequest = 6;

    /// <summary>段落数 &gt; 该值：直接降级 SidePanel（元素树与请求量都失控，AC 10）。</summary>
    public const int MaxInPlaceBlocks = 60;

    /// <summary>
    /// 按版面聚块。步骤：逐行按栏切分 → 逐段尝试并入已打开的块（水平重叠 + 行距 + 句末标点三条同时满足）
    /// → 块的原文按行连接 → 覆盖框 = 段框按实测系数扩边并钳制进图像。
    /// </summary>
    public static IReadOnlyList<OcrBlock> Group(
        IReadOnlyList<OcrLineBox> lines, double lineHeight, int imageWidth, int imageHeight)
    {
        if (lines is null || lines.Count == 0)
        {
            return [];
        }

        var height = lineHeight > 0 ? lineHeight : OcrLayoutRules.MedianLineHeight(lines);
        var builders = new List<Builder>();

        foreach (var line in lines.OrderBy(line => line.Rect.Top).ThenBy(line => line.Rect.Left))
        {
            foreach (var segment in SplitColumns(line, height))
            {
                var host = MatchBlock(builders, segment, height * LineGapRatio);
                if (host is null)
                {
                    host = new Builder();
                    builders.Add(host);
                }

                host.Add(segment);
            }
        }

        var ordered = builders
            .OrderBy(builder => builder.Lines.Min(line => line.Rect.Top))
            .ThenBy(builder => builder.Lines.Min(line => line.Rect.Left))
            .ToArray();

        var blocks = new List<OcrBlock>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var rect = OcrRect.Union(ordered[index].Lines.Select(line => line.Rect));
            blocks.Add(new OcrBlock(
                index,
                ordered[index].Lines.Select(line => line.LineIndex).ToArray(),
                rect,
                OcrLayoutRules.ExpandForCover(rect, height, imageWidth, imageHeight),
                JoinLines(ordered[index].Lines.Select(line => line.Text))));
        }

        return blocks;
    }

    /// <summary>整页是否检测到竖排文本。</summary>
    public static bool HasVerticalText(IReadOnlyList<OcrLineBox> lines) => OcrLayoutRules.HasVerticalText(lines);

    /// <summary>
    /// 多行文本的连接：中日韩之间**不加空格**（加了会在译文里出现突兀空格），其余用单个空格。
    /// </summary>
    public static string JoinLines(IEnumerable<string> texts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var raw in texts)
        {
            var text = raw?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                var previous = builder[^1];
                var first = text[0];
                // 只有「两侧都是中日韩」才不加空格：中文之间的空格是错的，
                // 而「中文 + English / English + 中文」之间保留空格对译文更友好
                if (!IsCjk(previous) || !IsCjk(first))
                {
                    builder.Append(' ');
                }
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>是否 CJK 字符（含全角标点与假名；用于连接时是否补空格）。</summary>
    public static bool IsCjk(char value) =>
        (value >= 0x3000 && value <= 0x303F)   // CJK 标点
        || (value >= 0x3040 && value <= 0x30FF) // 平假名 / 片假名
        || (value >= 0x3400 && value <= 0x4DBF)
        || (value >= 0x4E00 && value <= 0x9FFF)
        || (value >= 0xF900 && value <= 0xFAFF)
        || (value >= 0xFF00 && value <= 0xFFEF); // 全角字符

    /// <summary>该段是否以句末标点结尾（结尾引号/括号先剥掉再判）。</summary>
    public static bool EndsSentence(string? text)
    {
        var trimmed = text?.TrimEnd(' ', '"', '\'', '”', '’', ')', '）', ']', '】', '》');
        return !string.IsNullOrEmpty(trimmed) && SentenceEnders.Contains(trimmed[^1]);
    }

    /// <summary>同行内按栏切分：相邻词间隙 &gt; 1.5 × 行高即在此切开（多列 PDF、表格、导航栏）。</summary>
    private static List<Segment> SplitColumns(OcrLineBox line, double lineHeight)
    {
        var segments = new List<Segment>();
        var words = line.Words.OrderBy(word => word.Rect.Left).ToArray();
        if (words.Length == 0)
        {
            return segments;
        }

        var threshold = ColumnGapRatio * lineHeight;
        var start = 0;
        for (var i = 1; i <= words.Length; i++)
        {
            var cut = i == words.Length
                || words[i].Rect.Left - words[i - 1].Rect.Right > threshold;
            if (!cut)
            {
                continue;
            }

            var run = words[start..i];
            var text = i - start == words.Length
                ? line.Text // 整行未被切分时**一律取 OcrLine.Text**（词拼接会丢空格）
                : JoinLines(run.Select(word => word.Text));
            segments.Add(new Segment(line.Index, OcrRect.Union(run.Select(word => word.Rect)), text));
            start = i;
        }

        return segments;
    }

    /// <summary>为一段文本找宿主块：满足行距、非同一视觉行、上段未句末、水平重叠最大者。</summary>
    private static Builder? MatchBlock(List<Builder> builders, Segment segment, double maxGap)
    {
        Builder? best = null;
        var bestOverlap = 0.0;
        foreach (var builder in builders)
        {
            if (builder.HasLine(segment.LineIndex))
            {
                continue; // 同一行的另一栏：必须另起一块，否则两栏文本会被拼进一段
            }

            var last = builder.Last.Rect;
            if (segment.Rect.Top < last.Bottom - 0.5)
            {
                continue; // 同一视觉行（或更靠上）→ 不能合并，否则会把多栏拍成一段
            }

            if (segment.Rect.Top - last.Bottom > maxGap)
            {
                continue;
            }

            if (EndsSentence(builder.Last.Text))
            {
                continue;
            }

            var overlap = last.HorizontalOverlapRatio(segment.Rect);
            if (overlap < MinHorizontalOverlap || overlap <= bestOverlap)
            {
                continue;
            }

            bestOverlap = overlap;
            best = builder;
        }

        return best;
    }

    private readonly record struct Segment(int LineIndex, OcrRect Rect, string Text);

    private sealed class Builder
    {
        private readonly List<Segment> _segments = [];

        public IReadOnlyList<Segment> Lines => _segments;

        public Segment Last => _segments[^1];

        public bool HasLine(int lineIndex) => _segments.Any(segment => segment.LineIndex == lineIndex);

        public void Add(Segment segment) => _segments.Add(segment);
    }
}
