using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// OCR 布局的几何规则（FR-027 / 14.3.1）：词框 → 行框并集、坐标反缩放、扩边系数、旋转/竖排判定。
/// 系数的**实测依据**写在各常量注释里（本机 850 词实测，见 OcrLayoutRules）。
/// </summary>
public sealed class OcrLayoutTests
{
    private static OcrWordBox Word(string text, double left, double top, double width, double height) =>
        new(text, new OcrRect(left, top, left + width, top + height));

    private static OcrLineBox Line(int index, string text, params OcrWordBox[] words) =>
        new(index, text, words, OcrRect.Union(words.Select(word => word.Rect)));

    // ---------------- 词框 → 行框并集 ----------------

    [Fact]
    public void Union_取全部词框的最小外接矩形()
    {
        var rect = OcrRect.Union(new[]
        {
            new OcrRect(10, 20, 30, 32),
            new OcrRect(34, 18, 40, 33), // 同一行里上下有偏差的词（抗锯齿 / 下标）
        });

        Assert.Equal(10, rect.Left);
        Assert.Equal(18, rect.Top);
        Assert.Equal(40, rect.Right);
        Assert.Equal(33, rect.Bottom);
    }

    [Fact]
    public void Union_空集合与全空矩形都退化为空()
    {
        Assert.True(OcrRect.Union([]).IsEmpty);
        Assert.True(OcrRect.Union([new OcrRect(0, 0, 0, 0)]).IsEmpty);
    }

    [Fact]
    public void HorizontalOverlapRatio_按较窄者归一_并夹取到_0()
    {
        var a = new OcrRect(0, 0, 100, 20);
        Assert.Equal(1.0, a.HorizontalOverlapRatio(new OcrRect(0, 40, 100, 60)), 3);
        Assert.Equal(0.5, a.HorizontalOverlapRatio(new OcrRect(50, 40, 250, 60)), 3);
        Assert.Equal(0, a.HorizontalOverlapRatio(new OcrRect(120, 40, 200, 60)), 3);
    }

    [Fact]
    public void ToPixelRect_四舍五入且宽高至少_1px()
    {
        var rect = new OcrRect(10.4, 20.6, 10.9, 20.9).ToPixelRect();
        Assert.Equal(10, rect.X);
        Assert.Equal(21, rect.Y);
        Assert.Equal(1, rect.Width);
        Assert.Equal(1, rect.Height);
    }

    // ---------------- 旋转 / 竖排判定（实测：正常横排的 TextAngle 是 −0 而不是 null） ----------------

    [Theory]
    [InlineData(null, false)]
    [InlineData(-0.0, false)] // 本机实测值：正常横排也返回 −0，绝不能按「非 null 就降级」
    [InlineData(1.5, false)]
    [InlineData(3.0, false)]
    [InlineData(3.001, true)]
    [InlineData(-10.0, true)]
    [InlineData(-90.0, true)]
    public void IsTilted_按角度值判定而不是按是否为_null(double? angle, bool expected) =>
        Assert.Equal(expected, OcrLayoutRules.IsTilted(angle));

    [Fact]
    public void IsVerticalLine_高宽比大于_2_5_且词数不少于_2()
    {
        Assert.True(OcrLayoutRules.IsVerticalLine(Line(0, "竖排",
            Word("竖", 100, 0, 20, 60), Word("排", 100, 70, 20, 60))));

        // 高宽比够但只有一个词 → 不判竖排（可能是艺术字/图标）
        Assert.False(OcrLayoutRules.IsVerticalLine(Line(0, "字", Word("字", 100, 0, 20, 60))));

        // 词多但很宽 → 不是竖排
        Assert.False(OcrLayoutRules.IsVerticalLine(Line(0, "abc",
            Word("a", 0, 0, 20, 12), Word("b", 24, 0, 20, 12))));
    }

    [Fact]
    public void HasVerticalText_任一行竖排即为真()
    {
        var horizontal = Line(0, "hi", Word("hi", 0, 0, 40, 12));
        var vertical = Line(1, "竖排", Word("竖", 100, 0, 20, 60), Word("排", 100, 70, 20, 60));

        Assert.False(OcrLayoutRules.HasVerticalText([horizontal]));
        Assert.True(OcrLayoutRules.HasVerticalText([horizontal, vertical]));
    }

    // ---------------- 行高中位数与估算字号 ----------------

    [Fact]
    public void MedianLineHeight_奇偶与空集合都稳定()
    {
        Assert.Equal(1.0, OcrLayoutRules.MedianLineHeight([]));
        Assert.Equal(12, OcrLayoutRules.MedianLineHeight(
        [
            Line(0, "a", Word("a", 0, 0, 10, 8)),
            Line(1, "b", Word("b", 0, 20, 10, 12)),
            Line(2, "c", Word("c", 0, 40, 10, 20)),
        ]));
        Assert.Equal(11, OcrLayoutRules.MedianLineHeight(
        [
            Line(0, "a", Word("a", 0, 0, 10, 8)),
            Line(1, "b", Word("b", 0, 20, 10, 14)),
        ]));
    }

    [Fact]
    public void EstimatedFontPx_按墨水高换算()
    {
        Assert.Equal(16, OcrLayoutRules.EstimatedFontPx(12), 3); // 12 / 0.75
        Assert.Equal(1, OcrLayoutRules.EstimatedFontPx(0), 3);
    }

    // ---------------- 坐标反缩放（识别前等比缩小过） ----------------

    [Fact]
    public void Create_按_1_ratio_还原坐标并重算行框与下标()
    {
        var raw = new[]
        {
            Line(7, "ab", Word("a", 10, 20, 6, 8), Word("b", 18, 20, 6, 8)),
        };

        var layout = OcrLayoutRules.Create(raw, -0.0, 500, 400, 0.5);

        var line = Assert.Single(layout.Lines);
        Assert.Equal(20, line.Rect.Left);
        Assert.Equal(40, line.Rect.Top);
        Assert.Equal(48, line.Rect.Right);
        Assert.Equal(56, line.Rect.Bottom);
        Assert.Equal(0, line.Index); // 下标按保留下来的行重排
        Assert.Equal(16, layout.LineHeight); // 8 px 的行框高按 1/0.5 还原为 16
        Assert.Equal(500, layout.Width);
        Assert.Equal(0.5, layout.RestoreRatio);
    }

    [Fact]
    public void Create_未缩小_ratio_为_1_时坐标原样_且丢弃无词的行()
    {
        var layout = OcrLayoutRules.Create(
            [new OcrLineBox(0, "无词", [], default), Line(1, "x", Word("x", 5, 6, 7, 8))],
            null, 100, 100, 1.0);

        var line = Assert.Single(layout.Lines);
        Assert.Equal(5, line.Rect.Left);
        Assert.Equal(1.0, layout.RestoreRatio);
    }

    [Fact]
    public void Create_非法_ratio_退化为_1()
    {
        var layout = OcrLayoutRules.Create([Line(0, "x", Word("x", 5, 6, 7, 8))], null, 100, 100, 0);
        Assert.Equal(5, layout.Lines[0].Rect.Left);
        Assert.Equal(1.0, layout.RestoreRatio);
    }

    // ---------------- 覆盖扩边系数（实测校正值） ----------------

    [Fact]
    public void ExpandForCover_垂直_0_22_水平_0_20_倍行高()
    {
        var rect = new OcrRect(100, 200, 200, 212); // 行高 12
        var expanded = OcrLayoutRules.ExpandForCover(rect, 12, 1000, 1000);

        Assert.Equal(100 - (0.20 * 12), expanded.Left, 6);
        Assert.Equal(200 - (0.22 * 12), expanded.Top, 6);
        Assert.Equal(200 + (0.20 * 12), expanded.Right, 6);
        Assert.Equal(212 + (0.22 * 12), expanded.Bottom, 6);
    }

    [Fact]
    public void ExpandForCover_实测校正值_水平大于文档初值_垂直不小于_p99()
    {
        // 实测：垂直残留 p99≈0.11 → 0.22 充足；水平残留 p90≈0.167 → 文档初值 0.10 不足，校正为 0.20
        Assert.Equal(0.22, OcrLayoutRules.CoverVerticalRatio, 6);
        Assert.Equal(0.20, OcrLayoutRules.CoverHorizontalRatio, 6);
        Assert.True(OcrLayoutRules.CoverHorizontalRatio > 0.10);
    }

    [Fact]
    public void ExpandForCover_贴边时钳制进图像()
    {
        var expanded = OcrLayoutRules.ExpandForCover(new OcrRect(0, 0, 50, 12), 12, 60, 20);

        Assert.Equal(0, expanded.Left);
        Assert.Equal(0, expanded.Top);
        Assert.True(expanded.Right <= 60);
        Assert.True(expanded.Bottom <= 20);
    }
}
