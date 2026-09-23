using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

public sealed class GlossaryFileCodecTests
{
    [Fact]
    public void Csv_Roundtrip_EscapesCommaQuotesAndNewline()
    {
        var items = new[]
        {
            new GlossaryItem("a,b", "含\"引号\"", SourceLanguage: "en", TargetLanguage: "zh-CN",
                MatchMode: GlossaryMatchModes.Contains, Note: "多行\n备注"),
        };

        var csv = GlossaryFileCodec.ExportCsv(items);
        var parsed = GlossaryFileCodec.ParseCsv(csv);

        var item = Assert.Single(parsed.Items);
        Assert.Equal(items[0], item);
        Assert.Contains("\"a,b\"", csv);
        Assert.Contains("\"含\"\"引号\"\"\"", csv);
        Assert.Contains("\"多行\n备注\"", csv);
    }

    [Fact]
    public void Tsv_ParsesChineseHeadersAndAliases()
    {
        var tsv = "源词\t译法\t启用\t源语言\t目标语言\t匹配方式\t备注\r\n"
                  + "regular expression\t正则表达式\t是\t英语\t中文（简体）\t正则\t平台规则\r\n";

        var parsed = GlossaryFileCodec.ParseTsv(tsv);

        var item = Assert.Single(parsed.Items);
        Assert.Equal("regular expression", item.Source);
        Assert.Equal("正则表达式", item.Target);
        Assert.True(item.Enabled);
        Assert.Equal("en", item.SourceLanguage);
        Assert.Equal("zh-CN", item.TargetLanguage);
        Assert.Equal(GlossaryMatchModes.Regex, item.MatchMode);
        Assert.Equal("平台规则", item.Note);
    }

    [Fact]
    public void Parse_PositionalFileWithoutHeader_IsSupported()
    {
        var parsed = GlossaryFileCodec.Parse(
            "Ozon,Ozon商城,true,en,zh-CN,word,品牌保留",
            ',');

        var item = Assert.Single(parsed.Items);
        Assert.Equal("Ozon", item.Source);
        Assert.Equal("Ozon商城", item.Target);
        Assert.Equal("品牌保留", item.Note);
    }

    [Fact]
    public void Parse_SkipsExactDuplicates_AndWarnsAboutReverseConflict()
    {
        var existing = new[] { new GlossaryItem("memory", "内存") };
        var parsed = GlossaryFileCodec.ParseCsv(
            "源词,译法\nmemory,内存\n内存,memory\nmemory,RAM",
            existing);

        Assert.Equal(1, parsed.DuplicateCount);
        Assert.Equal(2, parsed.Items.Count);
        Assert.Contains(parsed.Warnings, warning => warning.Contains("反向冲突", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_UnclosedQuote_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            GlossaryFileCodec.ParseCsv("源词,译法\n\"memory,内存"));

        Assert.Contains("双引号", exception.Message, StringComparison.Ordinal);
    }
}
