using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>Google 非官方接口响应解析器单元测试（固定样本 JSON，无网络依赖）。</summary>
public class GoogleResponseParserTests
{
    [Fact]
    public void Parse_SingleSegment_ReturnsTextAndDetectedLanguage()
    {
        const string json = """[[["你好","hello",null,null,10]],null,"en"]""";

        var (translated, detected) = GoogleResponseParser.Parse(json);

        Assert.Equal("你好", translated);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_MultipleSegments_ConcatenatesInOrder()
    {
        const string json = """[[["你好，","hello, ",null,null,10],["世界","world",null,null,10]],null,"en"]""";

        var (translated, detected) = GoogleResponseParser.Parse(json);

        Assert.Equal("你好，世界", translated);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_DetectedChineseLanguage_ReturnsCode()
    {
        const string json = """[[["こんにちは","hello",null,null,10]],null,"zh-CN"]""";

        var (_, detected) = GoogleResponseParser.Parse(json);

        Assert.Equal("zh-CN", detected);
    }

    [Fact]
    public void Parse_MissingDetectedLanguage_ReturnsNull()
    {
        const string json = """[[["你好","hello",null,null,10]]]""";

        var (_, detected) = GoogleResponseParser.Parse(json);

        Assert.Null(detected);
    }

    [Fact]
    public void Parse_NonStringSegmentCells_AreSkipped()
    {
        // 段落第 0 列非字符串（null/数字）应跳过而不是崩溃
        const string json = """[[[null,"hello",null,null,10],[42,"world"]],null,"en"]""";

        var (translated, _) = GoogleResponseParser.Parse(json);

        Assert.Equal("", translated);
    }

    [Fact]
    public void Parse_EmptyTranslation_ReturnsEmptyText()
    {
        const string json = """[[],null,"en"]""";

        var (translated, detected) = GoogleResponseParser.Parse(json);

        Assert.Equal("", translated);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsTranslationException()
    {
        Assert.Throws<TranslationException>(() => GoogleResponseParser.Parse("{ not json"));
    }

    [Theory]
    [InlineData("\"not an array\"")]   // 字符串根节点
    [InlineData("[]")]                  // 空数组
    [InlineData("[123]")]               // 第 0 元素非数组
    [InlineData("{}")]                  // 对象根节点
    public void Parse_MalformedStructure_ThrowsTranslationException(string json)
    {
        var ex = Assert.Throws<TranslationException>(() => GoogleResponseParser.Parse(json));
        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }
}
