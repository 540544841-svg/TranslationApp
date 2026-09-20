using TranslationApp.Core.Speech;
using TranslationApp.Core.Vocabulary;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 批 5b 核心测试：FR-052 每日复习选题（纯轮转的稳定与轮转性）、
/// FR-053 句子切分（跟读要「一句一念」，碎句与超长句是真实痛点）。
/// </summary>
public class Batch5bCoreTests
{
    // ==================== FR-052 DailyReviewSelector ====================

    [Fact]
    public void DailyReview_SameDay_IsStable()
    {
        var day = new DateOnly(2026, 9, 21);

        Assert.Equal(DailyReviewSelector.Pick(day, 40), DailyReviewSelector.Pick(day, 40));
    }

    [Fact]
    public void DailyReview_ConsecutiveDays_RotateWithoutOverlap()
    {
        var today = DailyReviewSelector.Pick(new DateOnly(2026, 9, 21), 40);
        var tomorrow = DailyReviewSelector.Pick(new DateOnly(2026, 9, 22), 40);

        Assert.Equal(5, today.Count);
        Assert.Equal(5, tomorrow.Count);
        Assert.Equal(10, today.Concat(tomorrow).Distinct().Count());
        Assert.Equal((today[^1] + 1) % 40, tomorrow[0]);
    }

    [Fact]
    public void DailyReview_FewerWordsThanDailyQuota_GivesAllOnce()
    {
        var picked = DailyReviewSelector.Pick(new DateOnly(2026, 9, 21), 3);

        Assert.Equal(3, picked.Count);
        Assert.Equal(3, picked.Distinct().Count());
        Assert.All(picked, i => Assert.InRange(i, 0, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void DailyReview_NoWords_ReturnsEmpty(int total) =>
        Assert.Empty(DailyReviewSelector.Pick(new DateOnly(2026, 9, 21), total));

    [Theory]
    // 系统时钟早于轮转基准日：差值为负，取模必须仍落在 [0, total) 内
    [InlineData(2025, 1, 1)]
    [InlineData(2020, 6, 15)]
    [InlineData(2099, 12, 31)]
    public void DailyReview_IndicesAlwaysInRange(int year, int month, int day)
    {
        foreach (var total in new[] { 1, 7, 40, 501 })
        {
            Assert.All(DailyReviewSelector.Pick(new DateOnly(year, month, day), total),
                i => Assert.InRange(i, 0, total - 1));
        }
    }

    // ==================== FR-053 SentenceSplitter ====================

    [Fact]
    public void Split_ChineseSentences_BreaksOnTerminalPunctuation()
    {
        var result = SentenceSplitter.Split("这是一条测试译文。它由两句组成！第三句带问号？");

        Assert.Equal(
            ["这是一条测试译文。", "它由两句组成！", "第三句带问号？"],
            result);
    }

    [Fact]
    public void Split_KeepsClosingQuotesWithTheSentence()
    {
        var result = SentenceSplitter.Split("He said \"hello there.\" Then he left.");

        Assert.Equal(2, result.Count);
        Assert.EndsWith("there.\"", result[0]);
        Assert.Equal("Then he left.", result[1]);
    }

    [Fact]
    public void Split_BreaksOnNewlines()
    {
        var result = SentenceSplitter.Split("第一行内容\r\n第二行内容\n第三行");

        Assert.Equal(["第一行内容", "第二行内容", "第三行"], result);
    }

    [Fact]
    public void Split_MergesTinyFragmentsIntoPreviousSentence()
    {
        var result = SentenceSplitter.Split("这是一个足够长的句子。嗯。好的。");

        Assert.Single(result);
        Assert.Equal("这是一个足够长的句子。嗯。好的。", result[0]);
    }

    [Fact]
    public void Split_ClampsOverlongSentenceAtBoundary()
    {
        var long_ = string.Join(' ', Enumerable.Repeat("word", 120)); // 600 字符，无句末标点

        var result = SentenceSplitter.Split(long_);

        Assert.True(result.Count >= 3);
        Assert.All(result, s => Assert.True(s.Length <= SentenceSplitter.MaxSentenceChars, $"{s.Length} 未切断"));
        Assert.Equal(long_.Replace(" ", ""), string.Concat(result).Replace(" ", ""));
    }

    [Fact]
    public void Split_ClampsOverlongUnbrokenTextByHardCut()
    {
        var result = SentenceSplitter.Split(new string('字', 450));

        Assert.Equal(3, result.Count);
        Assert.Equal(450, result.Sum(s => s.Length));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \t ")]
    public void Split_BlankText_ReturnsEmpty(string text) =>
        Assert.Empty(SentenceSplitter.Split(text));

    [Fact]
    public void Split_NullText_ReturnsEmpty() => Assert.Empty(SentenceSplitter.Split(null));

    [Fact]
    public void Split_DoesNotBreakDecimals()
    {
        var result = SentenceSplitter.Split("价格为 3.5 美元，含税。");

        Assert.Single(result);
        Assert.Contains("3.5", result[0]);
    }

    [Fact]
    public void Split_TextWithoutAnyPunctuation_IsOneSentence()
    {
        Assert.Equal(["没有标点的一句话"], SentenceSplitter.Split("没有标点的一句话"));
    }
}
