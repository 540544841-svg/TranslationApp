using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 腾讯云 TMT 响应解析测试（13.1.2）。
/// 重点是「失败时 HTTP 仍为 200」：错误藏在 Response.Error 里，
/// 只看 HTTP 状态码会把密钥错误、额度用尽误判为成功。
/// </summary>
public class TencentResponseParserTests
{
    private const string SuccessJson =
        """{"Response":{"TargetText":"你好","Source":"en","Target":"zh","RequestId":"req-1"}}""";

    [Fact]
    public void Parse_Success_ReturnsTextAndDetectedSource()
    {
        var (text, detected) = TencentResponseParser.Parse(SuccessJson);

        Assert.Equal("你好", text);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_SuccessWithoutSource_ReturnsNullDetected()
    {
        var (text, detected) = TencentResponseParser.Parse(
            """{"Response":{"TargetText":"你好","Target":"zh"}}""");

        Assert.Equal("你好", text);
        Assert.Null(detected);
    }

    [Theory]
    [InlineData("AuthFailure.SignatureFailure")]
    [InlineData("AuthFailure.SignatureExpire")]
    [InlineData("AuthFailure.SecretIdNotFound")]
    [InlineData("AuthFailure.TokenFailure")]
    [InlineData("AuthFailure.UnauthorizedOperation")]
    public void Parse_AuthFailureCodes_AreInvalidKey(string code)
    {
        var ex = Assert.Throws<TranslationException>(() => TencentResponseParser.Parse(ErrorJson(code)));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains("腾讯云", ex.Message);
    }

    [Fact]
    public void Parse_SignatureExpire_MentionsSystemClock()
    {
        // 本机时钟偏差是 SignatureExpire 的常见根因，提示里必须带出这一条
        var ex = Assert.Throws<TranslationException>(
            () => TencentResponseParser.Parse(ErrorJson("AuthFailure.SignatureExpire")));

        Assert.Contains("系统时间", ex.Message);
    }

    [Theory]
    [InlineData("FailedOperation.NoFreeAmount")]
    [InlineData("FailedOperation.ServiceIsolate")]
    [InlineData("LimitExceeded")]
    [InlineData("RequestLimitExceeded")]
    public void Parse_QuotaCodes_AreQuotaExceeded(string code)
    {
        var ex = Assert.Throws<TranslationException>(() => TencentResponseParser.Parse(ErrorJson(code)));

        Assert.Equal(TranslationErrorType.QuotaExceeded, ex.ErrorType);
    }

    [Theory]
    [InlineData("UnsupportedOperation.UnsupportedLanguage")]
    [InlineData("UnsupportedOperation.TextTooLong")]
    [InlineData("InvalidParameter")]
    public void Parse_UnsupportedAndParameterCodes_AreEngine(string code)
    {
        var ex = Assert.Throws<TranslationException>(() => TencentResponseParser.Parse(ErrorJson(code)));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
        Assert.Contains("语言", ex.Message);
    }

    [Theory]
    [InlineData("InternalError")]
    [InlineData("InternalError.SystemError")]
    [InlineData("SomeBrandNewCode")]
    [InlineData("")]
    public void Parse_InternalAndUnknownCodes_AreEngine(string code)
    {
        var ex = Assert.Throws<TranslationException>(() => TencentResponseParser.Parse(ErrorJson(code)));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"Response":"unexpected"}""")]
    [InlineData("""{"NoResponse":{}}""")]
    public void Parse_MalformedOrEmpty_AreEngineErrors(string json)
    {
        var ex = Assert.Throws<TranslationException>(() => TencentResponseParser.Parse(json));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void Parse_EmptyTargetText_IsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(
            () => TencentResponseParser.Parse("""{"Response":{"TargetText":"","Source":"en"}}"""));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void MapErrorCode_KeepsUnlistedCodesInEngineBucket()
    {
        // 不新增枚举值：文档未列出的码一律归入 Engine（13.1.1）
        Assert.Equal(TranslationErrorType.Engine, TencentResponseParser.MapErrorCode("FailedOperation.NewThing"));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("zh", "zh-CN")]
    [InlineData("ja", "ja")]
    public void FromTencentCode_MapsBackToInternalCode(string engineCode, string expected) =>
        Assert.Equal(expected, TencentTranslator.FromTencentCode(engineCode));

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("fr", "fr")]
    [InlineData("de", "de")]
    [InlineData("ru", "ru")]
    [InlineData("es", "es")]
    public void ToTencentCode_FollowsLanguageTable(string internalCode, string expected) =>
        Assert.Equal(expected, TencentTranslator.ToTencentCode(internalCode));

    [Theory]
    [InlineData("th")]
    [InlineData("vi")]
    [InlineData("")]
    public void ToTencentCode_UnsupportedLanguage_ReturnsNull(string internalCode) =>
        Assert.Null(TencentTranslator.ToTencentCode(internalCode));

    private static string ErrorJson(string code) =>
        "{\"Response\":{\"Error\":{\"Code\":\"" + code + "\",\"Message\":\"引擎返回的信息\"},\"RequestId\":\"req-2\"}}";
}
