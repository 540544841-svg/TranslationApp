using TranslationApp.Core.Dictionary;
using TranslationApp.Core.History;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 批 6a 核心测试：FR-056 AI 词典返回的容错解析（模型不老实是常态）、
/// FR-057 历史会话分组（30 分钟窗口与标签文案）。
/// </summary>
public class Batch6aCoreTests
{
    // ==================== FR-056 AiDictionaryParser ====================

    [Fact]
    public void Parse_CleanJson_ReturnsEntry()
    {
        var entry = AiDictionaryParser.Parse(
            """{"wordhead":"abandon","phonetic":"/əˈbændən/","senses":["v. 放弃；抛弃","n. 放任"]}""");

        Assert.NotNull(entry);
        Assert.Equal("abandon", entry!.Wordhead);
        Assert.Equal("/əˈbændən/", entry.Phonetic);
        Assert.Equal(["v. 放弃；抛弃", "n. 放任"], entry.Senses);
        Assert.Equal("/əˈbændən/\nv. 放弃；抛弃\nn. 放任", entry.Definition);
    }

    [Fact]
    public void Parse_JsonFenceAndProse_StillParses()
    {
        var raw = "好的，以下是结果：\n```json\n{\"word\":\"kid\",\"senses\":[\"n. 小孩\",\"v. 戏弄\"]}\n```\n希望有帮助！";

        var entry = AiDictionaryParser.Parse(raw);

        Assert.NotNull(entry);
        Assert.Equal("kid", entry!.Wordhead);
        Assert.Equal(2, entry.Senses.Count);
    }

    [Fact]
    public void Parse_TrailingCommas_Repaired()
    {
        var entry = AiDictionaryParser.Parse("""{"wordhead":"dog","senses":["n. 狗",],"phonetic":"",}""");

        Assert.NotNull(entry);
        Assert.Equal(["n. 狗"], entry!.Senses);
    }

    [Fact]
    public void Parse_CommaInsideString_IsNotEaten()
    {
        var entry = AiDictionaryParser.Parse("""{"wordhead":"cat","senses":["n. 猫科动物, 俗指家猫"]}""");

        Assert.Equal("n. 猫科动物, 俗指家猫", Assert.Single(entry!.Senses));
    }

    [Fact]
    public void Parse_AltKeyNamesAndArrayWordhead_AreAccepted()
    {
        var entry = AiDictionaryParser.Parse("""{"term":["run"],"phonetics":"/rʌn/","meanings":"v. 跑"}""");

        Assert.Equal("run", entry!.Wordhead);
        Assert.Equal("/rʌn/", entry.Phonetic);
        Assert.Equal(["v. 跑"], entry.Senses);
    }

    [Fact]
    public void Parse_ObjectSenses_FlattenedIntoOneLine()
    {
        var entry = AiDictionaryParser.Parse(
            """{"wordhead":"book","senses":[{"type":"n.","meaning":"书"},{"type":"v.","meaning":"预订"}]}""");

        Assert.Equal(2, entry!.Senses.Count);
        Assert.Contains("预订", entry.Senses[1]);
    }

    [Fact]
    public void Parse_TooManySenses_TruncatedToSix()
    {
        var senses = string.Join(',', Enumerable.Range(1, 12).Select(i => $"\"释义{i}\""));

        var entry = AiDictionaryParser.Parse($$"""{"wordhead":"x","senses":[{{senses}}]}""");

        Assert.Equal(AiDictionaryParser.MaxSenses, entry!.Senses.Count);
        Assert.Equal("释义1", entry.Senses[0]);
        Assert.Equal("释义6", entry.Senses[^1]);
    }

    [Fact]
    public void Parse_LongSense_Truncated()
    {
        var long_ = new string('长', AiDictionaryParser.MaxSenseChars + 80);

        var entry = AiDictionaryParser.Parse($$"""{"wordhead":"x","senses":["{{long_}}"]}""");

        Assert.StartsWith(new string('长', AiDictionaryParser.MaxSenseChars), entry!.Senses[0]);
        Assert.EndsWith("…", entry.Senses[0]);
        Assert.Equal(AiDictionaryParser.MaxSenseChars + 1, entry.Senses[0].Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("模型今天不肯给 JSON")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"wordhead\":\"x\",\"senses\":[]}")]
    public void Parse_UnusableShapes_ReturnNull(string? raw) => Assert.Null(AiDictionaryParser.Parse(raw));

    [Fact]
    public void DictionaryRequest_PinsJsonOnlyContract()
    {
        var prompt = LlmPrompt.BuildDictionaryRequest("abandon", "zh-CN");

        Assert.Contains("abandon", prompt);
        Assert.Contains("中文（简体）", prompt);
        Assert.Contains("只输出一个 JSON 对象", prompt);
        Assert.Contains("\"senses\"", prompt);
    }

    // ==================== FR-057 HistoryGrouper ====================

    private static TranslationRecord Record(long id, int minutesAgo, string source = "原文") =>
        new(id, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(8)).AddMinutes(-minutesAgo),
            source, "译文", "en", "zh-CN", "Bing");

    [Fact]
    public void Group_Empty_ReturnsEmpty() => Assert.Empty(HistoryGrouper.Group([]));

    [Fact]
    public void Group_SingleRecord_IsOneGroup()
    {
        var groups = HistoryGrouper.Group([Record(1, 0)]);

        var group = Assert.Single(groups);
        Assert.Equal(1, group.Count);
        Assert.Contains("1 条", group.Label);
    }

    [Fact]
    public void Group_ThirtyMinutesApart_StayInSameWindow()
    {
        var groups = HistoryGrouper.Group([Record(1, 0), Record(2, 30)]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public void Group_ThirtyOneMinutesApart_BreaksIntoTwoGroups()
    {
        var groups = HistoryGrouper.Group([Record(1, 0), Record(2, 31)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(1, groups[0].Count);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void Group_ThreeSessions_SplitAndOrderedNewestFirst()
    {
        var records = new[] { Record(1, 0), Record(2, 10), Record(3, 90), Record(4, 95), Record(5, 200) };

        var groups = HistoryGrouper.Group(records);

        Assert.Equal(3, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
        Assert.Equal(1, groups[2].Count);
        Assert.True(groups[0].Start > groups[1].Start);
        Assert.True(groups[1].Start > groups[2].Start);
    }

    [Fact]
    public void Group_UnorderedInput_IsSortedBeforeGrouping()
    {
        var groups = HistoryGrouper.Group([Record(3, 200), Record(1, 0), Record(2, 90)]);

        Assert.Equal(3, groups.Count);
        Assert.Equal(1, groups[0].Records[0].Id);   // 最新一条（0 分钟前）在第一组
    }

    [Fact]
    public void Group_LabelCarriesFirstSourceSummary_Truncated()
    {
        var groups = HistoryGrouper.Group([Record(1, 0, "这是一段很长的原文用来验证组标签里只保留前二十四个字剩下的用省略号")]);

        Assert.Contains("…", groups[0].Label);
        Assert.DoesNotContain("剩下的用省略号", groups[0].Label);
    }

    [Fact]
    public void Group_LabelShowsTimeRangeAcrossDays()
    {
        var groups = HistoryGrouper.Group([Record(1, 0), Record(2, 20)]);

        // 组起于最早一条（含日期），止于最新一条（同日只显示时分）
        Assert.Matches(@"09-21 \d{2}:\d{2} – \d{2}:\d{2} · 2 条", groups[0].Label);
    }
}
