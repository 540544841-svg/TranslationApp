using System.Text;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 百度翻译引擎测试（13.1.3）：MD5 签名固定向量 + 响应解析 + 语言码映射。
/// 签名期望值由一份独立的参考实现（build/compute-signature-vectors.ps1）推算，与产品代码相互独立。
/// </summary>
public class BaiduTranslatorTests
{
    private const string AppId = "20250101000000001";
    private const string Secret = "Kx7Yq2Ws9Zn4";

    [Theory]
    // 拼接顺序必须是 appid → q → salt → 密钥，无分隔符；输出 32 位小写十六进制
    [InlineData("hello", "Ab3xY9kL2mQ7", "4b4a58ba053b7d766ee9fea508a65960")]
    [InlineData("hello world", "salt1234", "f251f0fe1dcc9b90d223cdeb984806f5")]
    [InlineData("你好，世界", "Zz9Aa1Bb2Cc3", "bb77c633494d259c534e5b277aa29789")]
    public void Sign_FixedVector_MatchesExpectedMd5(string q, string salt, string expected)
    {
        var sign = BaiduTranslator.Sign(AppId, q, salt, Secret);

        Assert.Equal(expected, sign);
        Assert.Equal(32, sign.Length);
        Assert.Equal(sign.ToLowerInvariant(), sign); // 必须小写
    }

    [Fact]
    public void Sign_UsesRawTextNotUrlEncoded()
    {
        // 13.1.3：签名用「未 URL 编码的原始 q」，编码只由表单层完成。
        // 若误用编码后的文本签名（例如把空格写成 +），会得到 54001 Invalid Sign。
        var raw = BaiduTranslator.Sign(AppId, "hello world", "salt1234", Secret);
        var encoded = BaiduTranslator.Sign(AppId, "hello+world", "salt1234", Secret);

        Assert.Equal("f251f0fe1dcc9b90d223cdeb984806f5", raw);
        Assert.NotEqual(raw, encoded);
    }

    [Fact]
    public void Sign_IsSensitiveToEveryConcatPart()
    {
        var baseline = BaiduTranslator.Sign(AppId, "hello", "salt1234", Secret);

        Assert.NotEqual(baseline, BaiduTranslator.Sign("1" + AppId, "hello", "salt1234", Secret));
        Assert.NotEqual(baseline, BaiduTranslator.Sign(AppId, "hello!", "salt1234", Secret));
        Assert.NotEqual(baseline, BaiduTranslator.Sign(AppId, "hello", "salt1234" + "x", Secret));
        Assert.NotEqual(baseline, BaiduTranslator.Sign(AppId, "hello", "salt1234", Secret + "x"));
    }

    [Fact]
    public void CreateSalt_IsRandomAlphanumericWithinLengthRange()
    {
        var salts = Enumerable.Range(0, 200).Select(_ => BaiduTranslator.CreateSalt()).ToArray();

        Assert.All(salts, salt =>
        {
            Assert.InRange(salt.Length, 8, 16); // 13.1.3：建议 8~16 位随机字母数字
            Assert.All(salt, c => Assert.True(char.IsAsciiLetterOrDigit(c), $"非字母数字字符：{c}"));
        });
        Assert.True(salts.Distinct().Count() > 190, "salt 重复率过高，随机性不足");
    }

    // ==================== 响应解析 ====================

    [Fact]
    public void Parse_Success_ReturnsTextAndDetectedSource()
    {
        var (text, detected) = BaiduResponseParser.Parse(
            """{"from":"en","to":"zh","trans_result":[{"src":"hello","dst":"你好"}]}""");

        Assert.Equal("你好", text);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_MultiLineResult_RestoresLineBreaks()
    {
        // 百度按换行拆成多条结果，拼接时必须还原换行，否则原文分段结构丢失
        var (text, _) = BaiduResponseParser.Parse(
            """{"from":"en","to":"zh","trans_result":[{"src":"hello","dst":"你好"},{"src":"world","dst":"世界"}]}""");

        Assert.Equal("你好\n世界", text);
    }

    [Theory]
    [InlineData("52003")]
    [InlineData("54001")]
    [InlineData("90107")]
    public void Parse_KeyErrorCodes_AreInvalidKey(string code)
    {
        var ex = Assert.Throws<TranslationException>(
            () => BaiduResponseParser.Parse(ErrorJson($"\"{code}\"", "Invalid Sign")));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains(code, ex.Message);
    }

    [Theory]
    [InlineData("54003")]
    [InlineData("54004")]
    [InlineData("54005")]
    public void Parse_QuotaCodes_AreQuotaExceeded(string code)
    {
        var ex = Assert.Throws<TranslationException>(
            () => BaiduResponseParser.Parse(ErrorJson($"\"{code}\"", "Limited")));

        Assert.Equal(TranslationErrorType.QuotaExceeded, ex.ErrorType);
    }

    [Fact]
    public void Parse_TimeoutCode_IsNetworkSoItGetsRetried()
    {
        // 52001 归入 Network，基类才会按网络类错误重试 1 次（13.1.3）
        var ex = Assert.Throws<TranslationException>(
            () => BaiduResponseParser.Parse(ErrorJson("\"52001\"", "Timeout")));

        Assert.Equal(TranslationErrorType.Network, ex.ErrorType);
    }

    [Theory]
    [InlineData("52002")]
    [InlineData("58000")]
    [InlineData("58001")]
    [InlineData("58002")]
    [InlineData("99999")]
    [InlineData("")]
    public void Parse_EngineAndUnknownCodes_AreEngine(string code)
    {
        var ex = Assert.Throws<TranslationException>(
            () => BaiduResponseParser.Parse(ErrorJson($"\"{code}\"", "Oops")));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void Parse_NumericErrorCode_IsHandledLikeString()
    {
        // 13.1.3：error_code 在不同版本下可能是字符串或数字，必须两种都能解析
        var ex = Assert.Throws<TranslationException>(
            () => BaiduResponseParser.Parse(ErrorJson("54001", "Invalid Sign")));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains("54001", ex.Message);
    }

    [Fact]
    public void Parse_NumericAndStringErrorCode_ProduceSameClassification()
    {
        Assert.Equal(
            BaiduResponseParser.MapErrorCode("52003"),
            BaiduResponseParser.MapErrorCode(BaiduResponseParser.ReadErrorCode(
                System.Text.Json.JsonDocument.Parse("""{"error_code":52003}""").RootElement.GetProperty("error_code"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"from":"en"}""")]
    [InlineData("""{"from":"en","trans_result":[]}""")]
    [InlineData("""{"from":"en","trans_result":[{"src":"hello","dst":""}]}""")]
    public void Parse_MalformedOrEmpty_AreEngineErrors(string json)
    {
        var ex = Assert.Throws<TranslationException>(() => BaiduResponseParser.Parse(json));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    // ==================== 语言码映射 ====================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "cht")]
    [InlineData("en", "en")]
    [InlineData("ja", "jp")]
    [InlineData("ko", "kor")]
    [InlineData("fr", "fra")]
    [InlineData("de", "de")]
    [InlineData("ru", "ru")]
    [InlineData("es", "spa")]
    public void ToBaiduCode_FollowsLanguageTable(string internalCode, string expected) =>
        Assert.Equal(expected, BaiduTranslator.ToBaiduCode(internalCode));

    [Theory]
    [InlineData("th")]
    [InlineData("vi")]
    [InlineData("")]
    public void ToBaiduCode_UnsupportedLanguage_ReturnsNull(string internalCode) =>
        Assert.Null(BaiduTranslator.ToBaiduCode(internalCode));

    [Theory]
    [InlineData("zh", "zh-CN")]
    [InlineData("cht", "zh-TW")]
    [InlineData("jp", "ja")]
    [InlineData("kor", "ko")]
    [InlineData("fra", "fr")]
    [InlineData("spa", "es")]
    [InlineData("en", "en")]
    public void FromBaiduCode_MapsBackToInternalCode(string engineCode, string expected) =>
        Assert.Equal(expected, BaiduTranslator.FromBaiduCode(engineCode));

    private static string ErrorJson(string codeLiteral, string message) =>
        $$"""{"error_code":{{codeLiteral}},"error_msg":"{{message}}"}""";
}
