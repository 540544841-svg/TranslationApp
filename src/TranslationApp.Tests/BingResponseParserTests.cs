using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>Bing 引擎响应解析测试（真实响应样本来自 2026-09-13 实测）。</summary>
public class BingResponseParserTests
{
    [Fact]
    public void Parse_NormalResponse_ReturnsTextAndDetectedLanguage()
    {
        const string json = """
        [{"translations":[{"text":"你好，世界，这是一个测试","to":"zh-Hans","transliteration":{"text":"Nǐ hǎo","script":"Latn"}}],"usedLLM":true,"detectedLanguage":{"language":"en"}}]
        """;

        var (text, detected) = BingResponseParser.Parse(json);

        Assert.Equal("你好，世界，这是一个测试", text);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_WithoutDetectedLanguage_ReturnsNullDetection()
    {
        const string json = """[{"translations":[{"text":"早上好","to":"zh-Hans"}]}]""";

        var (text, detected) = BingResponseParser.Parse(json);

        Assert.Equal("早上好", text);
        Assert.Null(detected);
    }

    [Fact]
    public void Parse_ErrorObject_ThrowsEngineError()
    {
        const string json = """{"statusCode":400,"errorMessage":"Invalid request"}""";

        var ex = Assert.Throws<TranslationException>(() => BingResponseParser.Parse(json));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{"translations":[]}]""")]
    public void Parse_InvalidResponses_ThrowEngineError(string json)
    {
        var ex = Assert.Throws<TranslationException>(() => BingResponseParser.Parse(json));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }
}
