using TranslationApp.Core.ClipboardFormats;
using TranslationApp.Core.History;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-043：段落切分与对齐（纯函数）。spec §1。</summary>
public class ParagraphAlignerTests
{
    [Fact]
    public void Split_BlankLineSeparators_ReturnsParagraphs()
    {
        var parts = ParagraphAligner.Split("a1\na2\n\nb1\n\nc1");
        Assert.Equal(["a1\na2", "b1", "c1"], parts);
    }

    [Fact]
    public void Split_NoBlankLines_FallsBackToSingleNewlines()
    {
        var parts = ParagraphAligner.Split("l1\nl2\nl3");
        Assert.Equal(["l1", "l2", "l3"], parts);
    }

    [Fact]
    public void Split_TrimsAndDropsEmpties()
    {
        Assert.Equal(["x", "y"], ParagraphAligner.Split("  x \n\n\n  y  \n"));
    }

    [Fact]
    public void Align_MatchingCountsAtLeastThree_PairsInOrder()
    {
        var pairs = ParagraphAligner.Align("a\n\nb\n\nc", "A\n\nB\n\nC");
        Assert.NotNull(pairs);
        Assert.Equal([("a", "A"), ("b", "B"), ("c", "C")], pairs!);
    }

    [Fact]
    public void Align_CountMismatch_ReturnsNull()
    {
        Assert.Null(ParagraphAligner.Align("a\n\nb\n\nc", "A\n\nB"));
    }

    [Fact]
    public void Align_FewerThanThreeParagraphs_ReturnsNull()
    {
        // 两段以下不值得开对照视图（整块读更顺）
        Assert.Null(ParagraphAligner.Align("a\n\nb", "A\n\nB"));
    }
}

/// <summary>FR-044：复制策略的三种剪贴板文本（纯函数）。spec §2。</summary>
public class ClipboardFormatsTests
{
    [Fact]
    public void Translation_Only()
    {
        Assert.Equal("译文", ClipboardContentBuilder.Build("原文", "译文", ClipboardCopyMode.TranslationOnly));
    }

    [Fact]
    public void Source_Only()
    {
        Assert.Equal("原文", ClipboardContentBuilder.Build("原文", "译文", ClipboardCopyMode.SourceOnly));
    }

    [Fact]
    public void Both_SeparatedByBlankLine()
    {
        Assert.Equal(
            "原文\n\n译文",
            ClipboardContentBuilder.Build("原文", "译文", ClipboardCopyMode.Both));
    }
}

/// <summary>FR-045：TM 相似句匹配（纯函数）。spec §3。</summary>
public class TmMatcherTests
{
    private static TmCandidate Cand(string source, string translated, int ageDaysAgo = 0) =>
        new(source, translated, DateTimeOffset.UtcNow.AddDays(-ageDaysAgo));

    [Fact]
    public void ExactMatch_WinsWithScoreOne()
    {
        var hit = TmMatcher.Find("hello world", [Cand("goodbye", "x"), Cand("hello world", "你好世界", 3)]);
        Assert.NotNull(hit);
        Assert.Equal(1.0, hit!.Score, 3);
        Assert.Equal("你好世界", hit.Entry.Translated);
    }

    [Fact]
    public void NearMatch_AboveThreshold_Hits()
    {
        // 只差一个标点：相似度 > 0.92
        var hit = TmMatcher.Find("The quick brown fox.", [Cand("The quick brown fox", "敏捷的棕色狐狸")]);
        Assert.NotNull(hit);
        Assert.True(hit!.Score >= TmMatcher.MinSimilarity);
    }

    [Fact]
    public void BelowThreshold_Misses()
    {
        Assert.Null(TmMatcher.Find("Totally different sentence here", [Cand("The quick brown fox", "x")]));
    }

    [Fact]
    public void LengthGapPreFilter_MissesFast()
    {
        // 长度差 >15% 直接不进比对（即便共享前缀）
        var longText = new string('a', 100);
        var shortText = new string('a', 80);
        Assert.Null(TmMatcher.Find(longText, [Cand(shortText, "x")]));
    }

    [Fact]
    public void MultipleCandidates_PicksBest()
    {
        var hit = TmMatcher.Find(
            "Save the file now",
            [
                Cand("Save the file now!", "差一个感叹号"),
                Cand("Save the file now", "精确命中"),
                Cand("Delete the file now!", "改词太多应落选"),
            ]);
        Assert.NotNull(hit);
        Assert.Equal("精确命中", hit!.Entry.Translated);
    }

    [Fact]
    public void EmptyCandidates_ReturnsNull()
    {
        Assert.Null(TmMatcher.Find("anything", []));
    }

    [Fact]
    public void WhitespaceInput_ReturnsNull()
    {
        Assert.Null(TmMatcher.Find("   ", [Cand("   ", "x")]));
    }
}
