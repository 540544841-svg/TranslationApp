using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 排版决策与降级（FR-027 / 14.3.5）：模式 A 的字号自适应与下限、模式 B 的触发阈值、
/// 模式 C 的各条触发条件、以及「取样失败 → 覆盖色为 null（界面回退兜底令牌）」。
/// 测量回调在这里被替换成确定性的假测量器，因此这些判据可以穷举单测，不依赖真机渲染。
/// </summary>
public sealed class OverlayLayoutTests
{
    private const int ImageWidth = 1000;

    private const int ImageHeight = 600;

    /// <summary>
    /// 假测量器：宽度 = 字符数 × 字号 × 0.6，按最大宽度折行；行高 = 字号 × 1.4。
    /// 与 WPF 的真实测量同量级，用来固定「放得下 / 放不下」的判据。
    /// </summary>
    private static (double Width, double Height) Measure(string text, double fontSizePx, double maxWidthPx)
    {
        if (string.IsNullOrEmpty(text) || fontSizePx <= 0)
        {
            return (0, 0);
        }

        var natural = text.Length * fontSizePx * 0.6;
        var width = Math.Min(natural, maxWidthPx);
        var lines = Math.Max(1, (int)Math.Ceiling(natural / Math.Max(1, maxWidthPx)));
        return (width, lines * fontSizePx * 1.4);
    }

    private static OcrBlock Block(double left, double top, double width, double height, string text = "src") =>
        new(0, [0], new OcrRect(left, top, left + width, top + height),
            new OcrRect(left, top, left + width, top + height), text);

    private static BackgroundSample Flat(uint argb = 0xFFF0F0F0u, bool busy = false) =>
        new(true, (byte)(argb & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)((argb >> 16) & 0xFF), 240, busy ? 40 : 1, busy);

    private static OverlayRequest Request(
        IReadOnlyList<OcrBlock> blocks,
        IReadOnlyList<string?> translations,
        IReadOnlyList<BackgroundSample>? backgrounds = null,
        double dpiScale = 1.0,
        string? forced = null,
        bool segmentMismatch = false,
        bool translationFailed = false,
        string? merged = null,
        int imageHeight = ImageHeight) =>
        new(blocks, translations, ImageWidth, imageHeight, dpiScale,
            backgrounds ?? Enumerable.Range(0, blocks.Count).Select(_ => Flat()).ToArray(),
            merged, forced, segmentMismatch, translationFailed);

    // ---------------- 字号下限（按 DPI 折算，不写死） ----------------

    [Theory]
    [InlineData(1.0, 11)]
    [InlineData(1.25, 14)]
    [InlineData(1.5, 17)] // 11 × 1.5 = 16.5 → 17（150% 屏上必须比 100% 屏更大，不能写死 9/11）
    [InlineData(2.0, 22)]
    [InlineData(0, 11)]   // 非法缩放退化为 100%
    public void 字号下限按_DPI_折算并保底_9px(double dpiScale, double expected) =>
        Assert.Equal(expected, OverlayLayout.MinFontPx(dpiScale), 3);

    // ---------------- 模式 A：单位替换 + 字号自适应 ----------------

    [Fact]
    public void 模式A_放得下时逐块原位替换_字号在上下限之间_并带上取样底色()
    {
        var block = Block(100, 100, 400, 40);
        var plan = OverlayLayout.Build(Request([block], ["译文"]), Measure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);
        Assert.Null(plan.StatusMessage);
        var placement = Assert.Single(plan.Blocks);
        Assert.Equal("译文", placement.Text);
        Assert.Equal(0xFFF0F0F0u, placement.CoverArgb);
        Assert.False(placement.Wrap);

        var upper = Math.Min(0.92 * 40, OcrLayoutRules.EstimatedFontPx(40) * 1.2);
        Assert.True(placement.FontSizePx > 0);
        Assert.True(placement.FontSizePx <= upper + 0.001);
        Assert.True(placement.FontSizePx >= OverlayLayout.MinFontPx(1.0));
    }

    [Fact]
    public void 模式A_字号从上限下探_但不会超过段框高的_0_92_倍()
    {
        var block = Block(0, 0, 500, 60);
        var plan = OverlayLayout.Build(Request([block], ["x"]), Measure);

        var placement = Assert.Single(plan.Blocks);
        Assert.True(placement.FontSizePx <= (0.92 * 60) + 0.001);
    }

    [Fact]
    public void 模式A_字号下限生效_低于下限不画不可读的小字而是降级()
    {
        // 段框只有 10 px 高：11 px 的下限字号必然放不下 → 不允许原地替换
        var block = Block(0, 0, 200, 10);
        var plan = OverlayLayout.Build(Request([block], ["这是一段很长的中文译文内容"]), Measure);

        Assert.NotEqual(PinOverlayMode.InPlace, plan.Mode);
        Assert.False(string.IsNullOrEmpty(plan.StatusMessage));
    }

    [Fact]
    public void 模式A_向下扩展段框可以救回放不下的段()
    {
        // 短段框（20 px）但下方净空充足：扩边后能放下，仍走原位替换
        var block = Block(0, 0, 300, 20);
        var plan = OverlayLayout.Build(Request([block], ["中文译文"]), Measure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);
        var placement = Assert.Single(plan.Blocks);
        Assert.True(placement.Rect.Height > 20); // 覆盖框被向下扩展了
    }

    [Fact]
    public void 模式A_每个块各自独立适配字号()
    {
        var small = Block(0, 0, 200, 20);
        var large = Block(0, 300, 600, 60);
        var plan = OverlayLayout.Build(Request([small, large], ["短", "长一些的译文"]), Measure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);
        Assert.Equal(2, plan.Blocks.Count);
        Assert.True(plan.Blocks[1].FontSizePx > plan.Blocks[0].FontSizePx);
    }

    [Fact]
    public void 取样失败时覆盖色为_null_界面据此回退兜底令牌()
    {
        var block = Block(0, 0, 300, 40);
        var plan = OverlayLayout.Build(
            Request([block], ["译文"], [BackgroundSample.Failed]), Measure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);
        Assert.Null(Assert.Single(plan.Blocks).CoverArgb);
    }

    // ---------------- 模式 B：整块排版 ----------------

    [Fact]
    public void 模式B_译文所需面积超过原文_1_6_倍时降级()
    {
        // 原文段框很小，译文在同样字号下面积远超 1.6 倍
        var block = Block(0, 0, 120, 20);
        var plan = OverlayLayout.Build(Request([block], ["a very long translated sentence here"]), Measure);

        Assert.NotEqual(PinOverlayMode.InPlace, plan.Mode);
        Assert.Contains("整块排版", plan.StatusMessage);
    }

    [Fact]
    public void 模式B_合并请求切分段数对不上时降级并说明()
    {
        var blocks = new[] { Block(0, 0, 200, 30), Block(0, 60, 200, 30) };
        var plan = OverlayLayout.Build(
            Request(blocks, [null, null], segmentMismatch: true, merged: "整段译文"), Measure);

        Assert.Equal(PinOverlayMode.BlockOverlay, plan.Mode);
        Assert.Equal(OverlayLayout.BlockOverlaySegments, plan.StatusMessage);
        var placement = Assert.Single(plan.Blocks);
        Assert.Equal("整段译文", placement.Text);
        Assert.True(placement.Wrap); // 整块排版一定换行显示
    }

    [Fact]
    public void 模式B_底板为全体段框的并集_且不越出图像()
    {
        var blocks = new[] { Block(100, 100, 200, 30), Block(500, 200, 200, 30) };
        var plan = OverlayLayout.Build(
            Request(blocks, ["甲", "乙"], segmentMismatch: true, merged: "甲乙"), Measure);

        var placement = Assert.Single(plan.Blocks);
        Assert.True(placement.Rect.X <= 100);
        Assert.True(placement.Rect.Y <= 100);
        Assert.True(placement.Rect.Right <= ImageWidth);
        Assert.True(placement.Rect.Bottom <= ImageHeight);
    }

    // ---------------- 模式 C：各条触发条件 ----------------

    [Fact]
    public void 模式C_倾斜文本()
    {
        var plan = OverlayLayout.Build(
            Request([Block(0, 0, 200, 30)], ["译文"], forced: OverlayLayout.SidePanelTilted), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelTilted, plan.StatusMessage);
        Assert.Empty(plan.Blocks);              // 不改动图片
        Assert.Equal("译文", plan.PanelText);
        Assert.True(plan.PanelHeightDip >= OverlayLayout.PanelMinDip);
    }

    [Fact]
    public void 模式C_竖排文本()
    {
        var plan = OverlayLayout.Build(
            Request([Block(0, 0, 30, 200)], ["译文"], forced: OverlayLayout.SidePanelVertical), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelVertical, plan.StatusMessage);
    }

    [Fact]
    public void 模式C_busy_段占比超过_40_百分比()
    {
        var blocks = new[] { Block(0, 0, 200, 30), Block(0, 60, 200, 30), Block(0, 120, 200, 30) };
        var plan = OverlayLayout.Build(
            Request(blocks, ["甲", "乙", "丙"], [Flat(busy: true), Flat(busy: true), Flat()]), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelBusy, plan.StatusMessage);
    }

    [Fact]
    public void 模式C_字段数超过_60_段()
    {
        var blocks = Enumerable.Range(0, 61)
            .Select(index => Block(0, index * 8, 200, 8, $"s{index}"))
            .ToArray();
        var plan = OverlayLayout.Build(
            Request(blocks, Enumerable.Repeat("译文", 61).ToArray()), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelTooManyBlocks, plan.StatusMessage);
    }

    [Fact]
    public void 模式C_裁剪图高度小于_60px()
    {
        var plan = OverlayLayout.Build(
            Request([Block(0, 0, 200, 20)], ["译文"], imageHeight: 40), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelTooSmall, plan.StatusMessage);
    }

    [Fact]
    public void 模式C_识别不到任何框()
    {
        var plan = OverlayLayout.Build(Request([], []), Measure);

        Assert.Equal(PinOverlayMode.SidePanel, plan.Mode);
        Assert.Equal(OverlayLayout.SidePanelNoBlocks, plan.StatusMessage);
    }

    [Theory]
    [InlineData(0, 0, true)]      // 倾斜
    [InlineData(1, 0, false)]     // 竖排
    [InlineData(2, 0, false)]     // busy > 40%
    [InlineData(3, 0, false)]     // 无框
    public void 各条降级都能被触发且一律带说明(int scenario, int _, bool expected)
    {
        OverlayPlan plan = scenario switch
        {
            0 => OverlayLayout.Build(Request([Block(0, 0, 200, 30)], ["译文"], forced: OverlayLayout.SidePanelTilted), Measure),
            1 => OverlayLayout.Build(Request([Block(0, 0, 30, 200)], ["译文"], forced: OverlayLayout.SidePanelVertical), Measure),
            2 => OverlayLayout.Build(Request([Block(0, 0, 200, 30)], ["译文"], [Flat(busy: true)]), Measure),
            _ => OverlayLayout.Build(Request([], []), Measure),
        };

        Assert.Equal(expected ? PinOverlayMode.SidePanel : PinOverlayMode.SidePanel, plan.Mode);
        Assert.False(string.IsNullOrWhiteSpace(plan.StatusMessage));
    }

    // ---------------- 翻译失败：仍然钉图，只显示原图 + 说明 ----------------

    [Fact]
    public void 翻译失败_不覆盖不面板_只给说明并保留可重试的信息()
    {
        var plan = OverlayLayout.Build(
            Request([Block(0, 0, 200, 30)], [null], translationFailed: true), Measure);

        Assert.Equal(PinOverlayMode.ImageOnly, plan.Mode);
        Assert.Empty(plan.Blocks);
        Assert.Null(plan.PanelText);
        Assert.False(plan.StatusMessage is null && plan.TranslatedText is null);
    }

    // ---------------- 面板高度估算 ----------------

    [Fact]
    public void 面板高度_下限_上限与随文本增长()
    {
        var min = OverlayLayout.EstimatePanelDipHeight("短", 1000, 600, 1.0);
        var more = OverlayLayout.EstimatePanelDipHeight(new string('中', 400), 1000, 600, 1.0);
        var max = 600 * OverlayLayout.PanelMaxHeightRatio;

        Assert.Equal(OverlayLayout.PanelMinDip, min, 3);
        Assert.True(more > min);
        Assert.True(more <= max + 0.001);
    }

    [Fact]
    public void 面板高度_空文本取下限() =>
        Assert.Equal(OverlayLayout.PanelMinDip, OverlayLayout.EstimatePanelDipHeight(null, 500, 400, 1.0), 3);

    [Fact]
    public void 面板高度_数值只依赖图片与文本规模_便于真机换算() 
    {
        var at100 = OverlayLayout.EstimatePanelDipHeight("abc", 800, 600, 1.0);
        var at200 = OverlayLayout.EstimatePanelDipHeight("abc", 800, 600, 2.0);

        // 2 倍缩放的屏上，同一张图的 DIP 高度减半，因此面板上限也随之收紧，但不会低于下限
        Assert.True(at200 <= at100 + 0.001);
        Assert.True(at200 >= OverlayLayout.PanelMinDip);
    }

    // ---------------- 面积比（模式 B 条件①）与「测量折行 → 渲染换行」的一致性 ----------------

    /// <summary>
    /// 真实排版量级的测量器：行高 = 字号 × 1.33（WPF <c>FormattedText</c> 的实际行距量级），
    /// 字宽 CJK = 1.0 em、拉丁 = 0.5 em。
    /// 它特意保留「行高含行距」这一点——历史缺陷正是分子用了这个含行距的实测高度、分母却用墨迹高度的段框，
    /// 两侧量纲不一致使比值被系统性放大 ≈1.78 倍（阈值 1.6 因此形同虚设）。
    /// </summary>
    private static (double Width, double Height) TypographicMeasure(string text, double fontSizePx, double maxWidthPx)
    {
        if (string.IsNullOrEmpty(text) || fontSizePx <= 0)
        {
            return (0, 0);
        }

        var advance = text.Sum(ch => OcrBlockGrouping.IsCjk(ch) ? fontSizePx : fontSizePx * 0.5);
        var lines = Math.Max(1, (int)Math.Ceiling(advance / Math.Max(1, maxWidthPx)));
        return (Math.Min(advance, Math.Max(1, maxWidthPx)), lines * fontSizePx * 1.33);
    }

    /// <summary>与生产同几何的段：覆盖框按实测系数扩边（<see cref="OcrLayoutRules.ExpandForCover"/>）。</summary>
    private static OcrBlock RealBlock(double left, double top, double width, double height, string text)
    {
        var rect = new OcrRect(left, top, left + width, top + height);
        return new OcrBlock(0, [0], rect, OcrLayoutRules.ExpandForCover(rect, height, ImageWidth, ImageHeight), text);
    }

    [Fact]
    public void 面积比_译文与原文一样长时约等于_1_不再被行距量纲放大()
    {
        // 段框宽 120 / 高 20（单行）：8 个拉丁字符在同一字号下一行正好放得下
        var block = Block(0, 0, 120, 20, "abcdefgh");
        var ratio = OverlayLayout.AreaRatio([block], [block.SourceText], TypographicMeasure);

        // 旧口径分子用的是含行距的实测高度（≈1.78 × 墨高），相同文字也会算出 ≈1.58（贴着 1.6 阈值），
        // 短选区的译文稍微宽一点就被误降级成整块排版；新口径按「实测行数 × 原文墨高」折算 → 比值只反映长度
        Assert.InRange(ratio, 0.5, 1.25);
    }

    [Fact]
    public void 面积比_译文确实长很多时仍触发整块排版()
    {
        var block = Block(0, 0, 120, 20, "abcdefgh");
        var ratio = OverlayLayout.AreaRatio([block], [new string('中', 40)], TypographicMeasure);

        Assert.True(ratio > OverlayLayout.AreaRatioLimit, $"面积比 {ratio:0.###} 应超过阈值");
    }

    [Fact]
    public void 面积比_真机形状的短选区_960x170_四段不再被误降级()
    {
        // 复现真机（选区 960×170 / 行高中位数 12 px → 估算字号 16 px / 4 段 / 每段译文都是短词）
        var blocks = new[]
        {
            RealBlock(24, 8, 100, 12, "Marketplace"),
            RealBlock(24, 42, 90, 12, "Groups"),
            RealBlock(24, 76, 120, 12, "TranslationApp"),
            RealBlock(24, 110, 100, 12, "Run Execute"),
        };
        var translations = new string?[] { "市场", "分组", "翻译应用", "运行执行" };

        var ratio = OverlayLayout.AreaRatio(blocks, translations, TypographicMeasure);
        Assert.True(ratio <= OverlayLayout.AreaRatioLimit, $"面积比 {ratio:0.###} 不该触发整块排版");

        var plan = OverlayLayout.Build(
            new OverlayRequest(blocks, translations, ImageWidth, 170, 1.0,
                Enumerable.Range(0, blocks.Length).Select(_ => Flat()).ToArray()),
            TypographicMeasure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);      // 逐段原位替换，而不是「整块排版」
        Assert.Null(plan.StatusMessage);                      // 不该出现「译文较长，已改用整块排版」
        Assert.Equal(4, plan.Blocks.Count);
        Assert.Equal(ratio, plan.AreaRatio!.Value, 6);        // 判定依据可复算（诊断日志与真机复验都用它）
    }

    [Fact]
    public void 模式A_测量时折行的块必须标记换行_否则渲染会被截断成半截译文()
    {
        // 两行的段框（60×60，行高 30）+ 需要折行的译文：测量（按宽折行、只校验总高）早已接受它，
        // 渲染若仍按「模式 A 一定是单行」用不换行 + 省略号，真机上就只剩「Marketplace G...」这类半截译文
        var rect = new OcrRect(200, 200, 260, 260);
        var block = new OcrBlock(
            0, [0, 1], rect, OcrLayoutRules.ExpandForCover(rect, 30, ImageWidth, ImageHeight), "src");

        var plan = OverlayLayout.Build(Request([block], ["四字译文"]), TypographicMeasure);

        Assert.Equal(PinOverlayMode.InPlace, plan.Mode);
        var placement = Assert.Single(plan.Blocks);
        Assert.True(placement.Wrap);                                       // 测量折行 → 渲染必须换行
        Assert.True(placement.FontSizePx >= OverlayLayout.MinFontPx(1.0));  // 且字号仍在下限之上
    }
}
