using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>阅读清洗测试（P0 批 1 / spec §3.1）。清洗只作用于 ≥80 字符的多行文本。</summary>
public class TextCleanerTests
{
    /// <summary>用后缀把样本垫到直通阈值以上，保证进入清洗逻辑。</summary>
    private static string Pad(string text) => text + "\n" + new string('x', 90);

    [Fact]
    public void ShortText_ReturnedUnchanged()
    {
        const string text = "under-\nscored";

        Assert.Equal(text, TextCleaner.CleanForReading(text));
    }

    [Fact]
    public void SingleLine_ReturnedUnchanged()
    {
        var text = new string('a', 200);

        Assert.Equal(text, TextCleaner.CleanForReading(text));
    }

    [Fact]
    public void JoinsHardLineBreak_MiddleOfSentence()
    {
        var cleaned = TextCleaner.CleanForReading(Pad("This is a\nbroken line"));

        Assert.Contains("This is a broken line", cleaned);
    }

    [Fact]
    public void JoinsAcrossHyphenation()
    {
        var cleaned = TextCleaner.CleanForReading(Pad("under-\nscored word"));

        Assert.Contains("underscored word", cleaned);
    }

    [Fact]
    public void DoesNotJoin_AfterSentenceEnd()
    {
        var cleaned = TextCleaner.CleanForReading(Pad("first sentence.\nsecond line"));

        Assert.Contains("first sentence.\nsecond line", cleaned);
    }

    [Fact]
    public void DoesNotJoin_WhenNextLineIsListItem()
    {
        var cleaned = TextCleaner.CleanForReading(Pad("ends with comma,\n- item one"));

        Assert.Contains("ends with comma,\n- item one", cleaned);
    }

    [Fact]
    public void ParagraphBreaksPreserved()
    {
        var cleaned = TextCleaner.CleanForReading(Pad("para one\n\npara two"));

        Assert.Contains("para one\n\npara two", cleaned);
    }

    [Fact]
    public void RepeatedHeaderLinesRemoved()
    {
        var text = "Annual Report 2026\nline one of body\ntakes a while\nto be long enough\n\n" +
                   "Annual Report 2026\nline two of body\ntakes a while\nto be long enough here too\n\n" +
                   "Annual Report 2026\nline three of the body text goes on and on\n" +
                   new string('y', 90);

        var cleaned = TextCleaner.CleanForReading(text);

        Assert.DoesNotContain("Annual Report 2026", cleaned);
        Assert.Contains("line one of body takes a while", cleaned);
    }

    [Fact]
    public void Idempotent()
    {
        var once = TextCleaner.CleanForReading(Pad("This is a\nbroken line\nwith-\nhyphens and more text to exceed the threshold easily"));

        Assert.Equal(once, TextCleaner.CleanForReading(once));
    }

    [Fact]
    public void NullOrEmpty_Safe()
    {
        Assert.Equal("", TextCleaner.CleanForReading(null!));
        Assert.Equal("", TextCleaner.CleanForReading(""));
    }

    [Fact]
    public void CrlfNormalized()
    {
        var cleaned = TextCleaner.CleanForReading("This is a\r\nbroken line\r\n" + new string('x', 90));

        Assert.Contains("This is a broken line", cleaned);
        Assert.DoesNotContain("\r", cleaned);
    }
}
