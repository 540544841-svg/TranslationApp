using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>批 6b 核心测试：FR-058 按应用记忆语言对的归一化、匹配优先级与学习上限。</summary>
public class AppLanguageRulesTests
{
    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("CHROME.EXE", "chrome")]
    [InlineData("  chrome  ", "chrome")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData("/usr/bin/firefox", "firefox")]
    [InlineData("WeChatAppEx", "wechatappex")]
    public void Normalize_StripsPathAndExeAndCase(string input, string expected) =>
        Assert.Equal(expected, AppLanguageRules.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".exe")]
    [InlineData("bad\u0001name")]
    public void Normalize_UnusableNames_BecomeEmpty(string? input) =>
        Assert.Equal("", AppLanguageRules.Normalize(input));

    [Fact]
    public void Normalize_RejectsOverlongName() =>
        Assert.Equal("", AppLanguageRules.Normalize(new string('a', 100)));

    [Fact]
    public void Match_IsCaseAndSuffixInsensitive()
    {
        var rules = new[] { new AppLanguagePair("Chrome.exe", "auto", "zh-CN") };

        Assert.NotNull(AppLanguageRules.Match(rules, "chrome"));
        Assert.NotNull(AppLanguageRules.Match(rules, @"C:\x\CHROME.EXE"));
        Assert.Null(AppLanguageRules.Match(rules, "firefox"));
    }

    [Fact]
    public void Match_FirstRuleWins_WhenSameProcessListedTwice()
    {
        var rules = new[]
        {
            new AppLanguagePair("chrome", "auto", "ru"),
            new AppLanguagePair("chrome", "auto", "zh-CN"),
        };

        Assert.Equal("ru", AppLanguageRules.Match(rules, "chrome")!.TargetLanguage);
    }

    [Fact]
    public void Learn_PutsNewRuleOnTop_AndOverwritesSameProcess()
    {
        var rules = new[]
        {
            new AppLanguagePair("chrome", "auto", "zh-CN"),
            new AppLanguagePair("notepad", "auto", "ru"),
        };

        var next = AppLanguageRules.Learn(rules, "CHROME.EXE", "en", "ru");

        Assert.Equal(2, next.Count);
        Assert.Equal("chrome", next[0].Process);
        Assert.Equal(("en", "ru"), (next[0].SourceLanguage, next[0].TargetLanguage));
    }

    [Fact]
    public void Learn_CapsAtMaxRules_DroppingOldest()
    {
        var rules = Enumerable.Range(0, AppLanguageRules.MaxRules)
            .Select(i => new AppLanguagePair($"app{i}", "auto", "zh-CN"))
            .ToList();

        var next = AppLanguageRules.Learn(rules, "newcomer", "auto", "en");

        Assert.Equal(AppLanguageRules.MaxRules, next.Count);
        Assert.Equal("newcomer", next[0].Process);
        Assert.Contains(next, r => r.Process == "app0");     // 置顶后最旧的恰好被挤掉一位
        Assert.DoesNotContain(next, r => r.Process == $"app{AppLanguageRules.MaxRules - 1}");
    }

    [Theory]
    [InlineData("", "en")]
    [InlineData("chrome", "")]
    [InlineData(null, "en")]
    public void Learn_IgnoresUnusableInput(string? process, string target)
    {
        var rules = new[] { new AppLanguagePair("chrome", "auto", "zh-CN") };

        Assert.Same(rules, AppLanguageRules.Learn(rules, process, "auto", target));
    }

    [Fact]
    public void Forget_RemovesMatchingProcessOnly()
    {
        var rules = new[]
        {
            new AppLanguagePair("chrome", "auto", "zh-CN"),
            new AppLanguagePair("notepad", "auto", "ru"),
        };

        var next = AppLanguageRules.Forget(rules, "Chrome.exe");

        Assert.Equal("notepad", Assert.Single(next).Process);
    }
}
