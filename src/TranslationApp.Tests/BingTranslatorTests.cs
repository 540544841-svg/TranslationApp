using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>Bing 引擎页面令牌解析与请求切块测试。</summary>
public class BingTranslatorTests
{
    private const string SampleHtml = """
    <html><head><script>
    var _G = { IG:"60141CE4F528407F9AB9D12430A90F98", EventID:"x" };
    </script></head>
    <body><div id="rich_tta" data-iid="translator.5023">
    params_AbusePreventionHelper = [1789269773882,"abcDEF1234567890abcDEF1234567890",3600000];
    </div></body></html>
    """;

    [Fact]
    public void ParseCredentials_ValidPage_ExtractsAllTokens()
    {
        var (ig, iid, key, token) = BingTranslator.ParseCredentials(SampleHtml);

        Assert.Equal("60141CE4F528407F9AB9D12430A90F98", ig);
        Assert.Equal("translator.5023", iid);
        Assert.Equal("1789269773882", key);
        Assert.Equal("abcDEF1234567890abcDEF1234567890", token);
    }

    [Theory]
    [InlineData("<html>无令牌</html>")]
    [InlineData("")]
    public void ParseCredentials_MissingTokens_ThrowsEngineError(string html)
    {
        var ex = Assert.Throws<TranslationException>(() => BingTranslator.ParseCredentials(html));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void SplitIntoChunks_ShortText_ReturnsSingleChunk()
    {
        var chunks = BingTranslator.SplitIntoChunks("hello world", 900).ToArray();

        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void SplitIntoChunks_LongText_SplitsWithinLimitAndPreservesContent()
    {
        var text = string.Concat(Enumerable.Repeat("This is a sentence. ", 200)); // 4000 字符

        var chunks = BingTranslator.SplitIntoChunks(text, 900).ToArray();

        Assert.True(chunks.Length > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 900, $"分块超长：{c.Length}"));
        Assert.Equal(text, string.Concat(chunks)); // 不丢字符
    }

    [Theory]
    [InlineData(TranslationLanguages.AutoCode, "zh-CN", "auto-detect", "zh-Hans")]
    [InlineData("en", "zh-TW", "en", "zh-Hant")]
    [InlineData("ja", "ko", "ja", "ko")]
    public void LanguageCodeMapping_ToBing(string source, string target, string expectedSource, string expectedTarget)
    {
        if (source != TranslationLanguages.AutoCode)
        {
            Assert.Equal(expectedSource, BingTranslator.ToBingCode(source));
        }
        Assert.Equal(expectedTarget, BingTranslator.ToBingCode(target));
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("en", "en")]
    public void LanguageCodeMapping_FromBing(string bingCode, string expected) =>
        Assert.Equal(expected, BingTranslator.FromBingCode(bingCode));

    [Fact]
    public void TimeoutFor_ShortText_StaysNearBaseTimeout()
    {
        var timeout = BingTranslator.TimeoutFor("hello");

        Assert.True(timeout >= TimeSpan.FromSeconds(8), $"短文本预算不应低于 8s，实际 {timeout}");
        Assert.True(timeout < TimeSpan.FromSeconds(8.5), $"短文本预算不应明显超过 8s，实际 {timeout}");
    }

    [Fact]
    public void TimeoutFor_LongText_ScalesUpButCapped()
    {
        var medium = BingTranslator.TimeoutFor(new string('a', 450));
        var full = BingTranslator.TimeoutFor(new string('a', 900));
        var over = BingTranslator.TimeoutFor(new string('a', 5000));

        Assert.True(medium > TimeSpan.FromSeconds(8) && medium < full);
        Assert.Equal(TimeSpan.FromSeconds(20), full);
        Assert.Equal(full, over); // 超过单块上限不再增长
    }
}
