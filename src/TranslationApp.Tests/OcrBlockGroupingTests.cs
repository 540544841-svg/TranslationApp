using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 行/段重组（FR-027 / 14.3.2）：按块翻译的前提是先把视觉行聚成版面块。
/// 三条合并条件的阈值都有实测支撑（行间距 p50=0.67×行高、p90=1.92×行高；行内词间隙 p90=0.83×行高）。
/// </summary>
public sealed class OcrBlockGroupingTests
{
    private const double LineHeight = 12;

    private static OcrLineBox Line(int index, string text, double left, double top, double width, double height = 12) =>
        new(index, text,
            [new OcrWordBox(text, new OcrRect(left, top, left + width, top + height))],
            new OcrRect(left, top, left + width, top + height));

    private static OcrLineBox MultiWordLine(
        int index, string text, double top, params (string Text, double Left, double Right)[] words)
    {
        var boxes = words
            .Select(word => new OcrWordBox(word.Text, new OcrRect(word.Left, top, word.Right, top + 12)))
            .ToArray();
        return new OcrLineBox(index, text, boxes, OcrRect.Union(boxes.Select(box => box.Rect)));
    }

    // ---------------- 段内合并 ----------------

    [Fact]
    public void 相邻两行_水平重叠且行距足够_且上句未结束_合成一段()
    {
        var blocks = OcrBlockGrouping.Group(
            [Line(0, "the quick brown", 100, 100, 200), Line(1, "fox jumps over", 100, 113, 200)],
            LineHeight, 1000, 1000);

        var block = Assert.Single(blocks);
        Assert.Equal([0, 1], block.LineIndexes);
        Assert.Equal("the quick brown fox jumps over", block.SourceText);
        Assert.Equal(100, block.Rect.Left);
        Assert.Equal(113 + 12, block.Rect.Bottom);
    }

    [Fact]
    public void 上一行以句末标点结尾_不并入下一行()
    {
        var blocks = OcrBlockGrouping.Group(
            [Line(0, "Ends here.", 100, 100, 200), Line(1, "New sentence", 100, 113, 200)],
            LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("Ends here.", blocks[0].SourceText);
        Assert.Equal("New sentence", blocks[1].SourceText);
    }

    [Theory]
    [InlineData('。')]
    [InlineData('！')]
    [InlineData('？')]
    [InlineData(':')]
    [InlineData(';')]
    public void 句末标点判定_含中文标点(char ender) =>
        Assert.True(OcrBlockGrouping.EndsSentence($"这是一句话{ender}"));

    [Fact]
    public void 句末标点判定_结尾引号先剥掉_空文本不算()
    {
        Assert.True(OcrBlockGrouping.EndsSentence("He said \"done.\""));
        Assert.False(OcrBlockGrouping.EndsSentence("no ending here"));
        Assert.False(OcrBlockGrouping.EndsSentence(""));
        Assert.False(OcrBlockGrouping.EndsSentence(null));
    }

    [Fact]
    public void 行间距超过_1_6_倍行高_拆成两段()
    {
        // 间距 = 30 px > 1.6 × 12 = 19.2
        var blocks = OcrBlockGrouping.Group(
            [Line(0, "first part", 100, 100, 200), Line(1, "second part", 100, 142, 200)],
            LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void 水平重叠率低于_0_5_拆成两段()
    {
        var blocks = OcrBlockGrouping.Group(
            [Line(0, "left column text", 0, 100, 60), Line(1, "right column text", 400, 113, 60)],
            LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
    }

    // ---------------- 多栏 / 表格：同行栏切分 ----------------

    [Fact]
    public void 同行词间隙大于_1_5_倍行高_在此切栏()
    {
        // 间隙 = 60 - 30 = 30 > 1.5 × 12 = 18
        var line = MultiWordLine(0, "ab cd", 100, ("ab", 0, 30), ("cd", 60, 90));
        var blocks = OcrBlockGrouping.Group([line], LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("ab", blocks[0].SourceText);
        Assert.Equal("cd", blocks[1].SourceText);
    }

    [Fact]
    public void 同行词间隙小于阈值_不切栏_且文本仍取_OcrLine_Text_以保留空格()
    {
        var line = MultiWordLine(0, "ab cd", 100, ("ab", 0, 30), ("cd", 34, 60));
        var blocks = OcrBlockGrouping.Group([line], LineHeight, 1000, 1000);

        var block = Assert.Single(blocks);
        Assert.Equal("ab cd", block.SourceText);
    }

    [Fact]
    public void 两栏分别成段_不会把两栏文本拍进同一段()
    {
        // 左右两栏各两行，交替给出（模拟 OCR 的阅读顺序）
        var lines = new[]
        {
            MultiWordLine(0, "L1 R1", 100, ("L1", 0, 40), ("R1", 300, 340)),
            MultiWordLine(1, "L2 R2", 113, ("L2", 0, 40), ("R2", 300, 340)),
        };

        var blocks = OcrBlockGrouping.Group(lines, LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("L1 L2", blocks[0].SourceText);
        Assert.Equal("R1 R2", blocks[1].SourceText);
    }

    // ---------------- 覆盖框与文本连接 ----------------

    [Fact]
    public void 覆盖框按实测系数扩边并钳制进图像()
    {
        var blocks = OcrBlockGrouping.Group([Line(0, "abc", 100, 100, 50)], LineHeight, 1000, 1000);
        var block = Assert.Single(blocks);

        Assert.Equal(100 - (0.20 * 12), block.CoverRect.Left, 6);
        Assert.Equal(100 - (0.22 * 12), block.CoverRect.Top, 6);
        Assert.Equal(150 + (0.20 * 12), block.CoverRect.Right, 6);
        Assert.Equal(112 + (0.22 * 12), block.CoverRect.Bottom, 6);
    }

    [Fact]
    public void 覆盖框不越出图像边界()
    {
        var blocks = OcrBlockGrouping.Group([Line(0, "abc", 0, 0, 50)], LineHeight, 60, 20);
        var block = Assert.Single(blocks);

        Assert.Equal(0, block.CoverRect.Left);
        Assert.Equal(0, block.CoverRect.Top);
        Assert.True(block.CoverRect.Right <= 60);
        Assert.True(block.CoverRect.Bottom <= 20);
    }

    [Fact]
    public void 文本连接_中文不加空格_英文加单个空格()
    {
        Assert.Equal("第一行第二行", OcrBlockGrouping.JoinLines(["第一行", "第二行"]));
        Assert.Equal("first line", OcrBlockGrouping.JoinLines(["first", "line"]));
        Assert.Equal("中文 English 中文", OcrBlockGrouping.JoinLines(["中文", "English", "中文"]));
        Assert.Equal("abc", OcrBlockGrouping.JoinLines(["abc", "", "  "]));
    }

    [Theory]
    [InlineData('中', true)]
    [InlineData('。', true)]
    [InlineData('あ', true)]
    [InlineData('a', false)]
    [InlineData('1', false)]
    [InlineData(' ', false)]
    public void IsCjk_判定(char value, bool expected) =>
        Assert.Equal(expected, OcrBlockGrouping.IsCjk(value));

    [Fact]
    public void 空输入与单行都稳定()
    {
        Assert.Empty(OcrBlockGrouping.Group([], LineHeight, 100, 100));

        var block = Assert.Single(OcrBlockGrouping.Group([Line(0, "solo", 10, 10, 40)], LineHeight, 100, 100));
        Assert.Equal("solo", block.SourceText);
        Assert.Equal(0, block.Index);
        Assert.Equal(40, block.EstimatedFontPx > 0 ? 40 : 0);
    }

    [Fact]
    public void 块按阅读顺序排序_且序号连续()
    {
        var blocks = OcrBlockGrouping.Group(
            [
                Line(0, "second", 100, 300, 200),
                Line(1, "first", 100, 100, 200),
            ],
            LineHeight, 1000, 1000);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("first", blocks[0].SourceText);
        Assert.Equal("second", blocks[1].SourceText);
        Assert.Equal([0, 1], blocks.Select(block => block.Index));
    }

    [Fact]
    public void 竖排文本_整页判定传给调用方() =>
        Assert.True(OcrBlockGrouping.HasVerticalText(
        [
            new OcrLineBox(0, "竖排",
                [
                    new OcrWordBox("竖", new OcrRect(100, 0, 120, 60)),
                    new OcrWordBox("排", new OcrRect(100, 70, 120, 130)),
                ],
                new OcrRect(100, 0, 120, 130)),
        ]));
}
