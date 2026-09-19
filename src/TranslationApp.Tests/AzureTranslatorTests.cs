using System.Net;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// Azure Translator V3 引擎测试（13.1.4）：自动检测（省略 from）、请求头与请求体、
/// 响应解析、错误归类（HTTP 状态码为主 / error.code 为辅）、语言码映射。
/// 其中「无凭据实测响应」用例的 JSON 取自 build/probe-engines.ps1 的真实返回值（2026-09-13）。
/// </summary>
public class AzureTranslatorTests
{
    private const string SubKey = "0123456789abcdef0123456789abcdef";

    /// <summary>实测返回：无凭据时 HTTP 401 + 该正文（error.code 为 401001，非文档示例的 401000）。</summary>
    private const string ObservedUnauthorizedBody =
        """{"error":{"code":401001,"message":"The request is not authorized because credentials are missing or invalid."}}""";

    // ==================== 请求构造 ====================

    [Fact]
    public void BuildRequestUri_AutoSource_OmitsFromParameter()
    {
        // 13.1.4：自动检测 = 省略 from（不要传 from=auto，是否接受未实测）
        var uri = AzureTranslator.BuildRequestUri("auto", "zh-Hans");

        Assert.DoesNotContain("from=", uri.Query);
        Assert.Contains("api-version=3.0", uri.Query);
        Assert.Contains("to=zh-Hans", uri.Query);
        Assert.Equal("https://api.cognitive.microsofttranslator.com/translate", uri.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public void BuildRequestUri_ExplicitSource_SendsFromParameter()
    {
        var uri = AzureTranslator.BuildRequestUri("en", "zh-Hans");

        Assert.Contains("from=en", uri.Query);
        Assert.Contains("to=zh-Hans", uri.Query);
    }

    [Fact]
    public void BuildRequestBody_IsJsonArrayWithTextProperty()
    {
        // 13.1.4：请求体是 JSON 数组 [{"Text":"hello"}]，只发 1 个元素
        var body = AzureTranslator.BuildRequestBody("hello");

        Assert.Equal("""[{"Text":"hello"}]""", body);
    }

    [Fact]
    public void CreateRequest_SendsSubscriptionKeyHeader()
    {
        var translator = CreateTranslator(Configured(), region: "eastasia");

        using var request = translator.CreateRequest("hello", "en", "zh-Hans");

        Assert.True(request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var keys));
        Assert.Equal(SubKey, Assert.Single(keys));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void CreateRequest_WithRegion_AddsRegionHeader()
    {
        var translator = CreateTranslator(Configured(), region: " eastasia ");

        using var request = translator.CreateRequest("hello", "en", "zh-Hans");

        Assert.True(request.Headers.TryGetValues("Ocp-Apim-Subscription-Region", out var regions));
        Assert.Equal("eastasia", Assert.Single(regions)); // 两端空白已去除
    }

    [Fact]
    public void CreateRequest_WithoutRegion_OmitsRegionHeader()
    {
        // 13.1.4：单服务资源可留空，此时不发 Region 头
        var translator = CreateTranslator(Configured(), region: "");

        using var request = translator.CreateRequest("hello", "en", "zh-Hans");

        Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Region"));
    }

    [Fact]
    public void CreateRequest_AutoSource_HasNoFromInUrl()
    {
        var translator = CreateTranslator(Configured());

        using var request = translator.CreateRequest("hello", "auto", "zh-Hans");

        Assert.DoesNotContain("from=", request.RequestUri!.Query);
    }

    // ==================== 响应解析 ====================

    [Fact]
    public void Parse_Success_ReturnsTextAndDetectedLanguage()
    {
        var (text, detected) = AzureResponseParser.Parse(
            """[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]""");

        Assert.Equal("你好", text);
        Assert.Equal("en", detected);
    }

    [Fact]
    public void Parse_WithoutDetectedLanguage_ReturnsNullDetected()
    {
        var (text, detected) = AzureResponseParser.Parse(
            """[{"translations":[{"text":"你好","to":"zh-Hans"}]}]""");

        Assert.Equal("你好", text);
        Assert.Null(detected);
    }

    [Fact]
    public void Parse_EmptyArray_ThrowsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(() => AzureResponseParser.Parse("[]"));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void Parse_ErrorObjectInBody_ThrowsClassified()
    {
        var ex = Assert.Throws<TranslationException>(
            () => AzureResponseParser.Parse(ObservedUnauthorizedBody));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
    }

    [Fact]
    public void TryReadError_ParsesNumericAndStringCodes()
    {
        var (numericCode, numericMessage) = AzureResponseParser.TryReadError(ObservedUnauthorizedBody);
        Assert.Equal("401001", numericCode);
        Assert.Contains("credentials", numericMessage);

        var (stringCode, _) = AzureResponseParser.TryReadError("""{"error":{"code":"400036"}}""");
        Assert.Equal("400036", stringCode);
    }

    [Fact]
    public void TryReadError_NonErrorBody_ReturnsNulls()
    {
        Assert.Equal((null, null), AzureResponseParser.TryReadError("""[{"translations":[]}]"""));
        Assert.Equal((null, null), AzureResponseParser.TryReadError("not json"));
        Assert.Equal((null, null), AzureResponseParser.TryReadError(""));
    }

    // ==================== 错误映射（HTTP 状态码为主） ====================

    [Theory]
    [InlineData(401, TranslationErrorType.InvalidKey)]        // 凭证缺失或无效
    [InlineData(403, TranslationErrorType.QuotaExceeded)]     // 订阅无权限 / 额度耗尽
    [InlineData(429, TranslationErrorType.QuotaExceeded)]     // 请求过多
    [InlineData(400, TranslationErrorType.Engine)]            // 语言不支持等
    [InlineData(500, TranslationErrorType.Engine)]
    [InlineData(503, TranslationErrorType.Engine)]
    public void MapStatus_MatchesDocumentTable(int status, TranslationErrorType expected)
    {
        Assert.Equal(expected, AzureResponseParser.MapStatus((HttpStatusCode)status));
    }

    [Theory]
    [InlineData("401000", TranslationErrorType.InvalidKey)]
    [InlineData("401001", TranslationErrorType.InvalidKey)] // 实测值
    [InlineData("401002", TranslationErrorType.InvalidKey)]
    [InlineData("403001", TranslationErrorType.QuotaExceeded)]
    [InlineData("429001", TranslationErrorType.QuotaExceeded)]
    [InlineData("429002", TranslationErrorType.QuotaExceeded)]
    public void MapErrorCode_AuxiliaryTable(string code, TranslationErrorType expected)
    {
        Assert.Equal(expected, AzureResponseParser.MapErrorCode(code));
    }

    [Fact]
    public void MapErrorCode_UnknownCode_ReturnsNull()
    {
        // 未知错误码不抢状态码的主导权
        Assert.Null(AzureResponseParser.MapErrorCode("999999"));
        Assert.Null(AzureResponseParser.MapErrorCode(null));
        Assert.Null(AzureResponseParser.MapErrorCode(""));
    }

    [Fact]
    public void ResolveErrorType_StatusWinsOverErrorCode()
    {
        // 403 与 error.code 401001 冲突时以 HTTP 状态码为准（13.1.4「状态码为主」）
        Assert.Equal(
            TranslationErrorType.QuotaExceeded,
            AzureResponseParser.ResolveErrorType(HttpStatusCode.Forbidden, "401001"));
    }

    [Fact]
    public void ResolveErrorType_FallsBackToErrorCodeWhenStatusIsEngine()
    {
        Assert.Equal(
            TranslationErrorType.InvalidKey,
            AzureResponseParser.ResolveErrorType(HttpStatusCode.BadRequest, "401001"));
    }

    [Fact]
    public void CreateError_ObservedUnauthorized_MentionsKeyAndCodesWithoutKeyLeak()
    {
        var ex = AzureResponseParser.CreateError(
            HttpStatusCode.Unauthorized, "401001", "credentials are missing or invalid");

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains("Azure 密钥无效或未授权", ex.Message);
        Assert.Contains("401001", ex.Message);
        Assert.DoesNotContain(SubKey, ex.Message);
    }

    // ==================== 语言码映射 ====================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("fr", "fr")]
    [InlineData("de", "de")]
    [InlineData("ru", "ru")]
    [InlineData("es", "es")]
    public void ToAzureCode_MatchesLanguageTable(string internalCode, string expected)
    {
        Assert.Equal(expected, AzureTranslator.ToAzureCode(internalCode));
    }

    [Fact]
    public void ToAzureCode_UnsupportedLanguage_ReturnsNull()
    {
        // 映射缺失必须明确报「不支持该语言」，不得静默传错码（13.1.7）
        Assert.Null(AzureTranslator.ToAzureCode("th"));
    }

    [Fact]
    public async Task TranslateAsync_UnsupportedLanguage_ThrowsBeforeAnyRequest()
    {
        var translator = CreateTranslator(Configured());

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", "th", "zh-CN"));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
        Assert.Contains("不支持该源语言", ex.Message);
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    public void FromAzureCode_MapsBackToInternal(string engineCode, string expected)
    {
        Assert.Equal(expected, AzureTranslator.FromAzureCode(engineCode));
    }

    // ==================== 配置判定与代理归属 ====================

    [Fact]
    public void IsConfigured_RequiresDecryptableSecret()
    {
        Assert.False(CreateTranslator(new AppSettings()).IsConfigured);
        Assert.False(CreateTranslator(new AppSettings { AzureSubscriptionKeyEncrypted = "plaintext" }).IsConfigured);
        Assert.True(CreateTranslator(new AppSettings
        {
            AzureSubscriptionKeyEncrypted = SecretStore.Protect(SubKey),
        }).IsConfigured);
    }

    [Fact]
    public void ProxyScope_IsForeignEngine()
    {
        // 13.1.1：Azure 归入「仅国外引擎」作用域
        Assert.Equal(ProxyScope.GoogleOnly, CreateTranslator(Configured()).Proxy);
    }

    [Fact]
    public void IdAndName_AreStable()
    {
        var translator = CreateTranslator(Configured());

        Assert.Equal("azure", translator.Id); // 配置文件中持久化，勿随意变更
        Assert.Equal("Azure Translator", translator.Name);
    }

    private static AppSettings Configured() => new()
    {
        AzureSubscriptionKeyEncrypted = SecretStore.Protect(SubKey),
    };

    private static AzureTranslator CreateTranslator(AppSettings settings, string? region = null)
    {
        if (region is not null)
        {
            settings.AzureRegion = region;
        }

        return new AzureTranslator(settings, new HttpClientProvider());
    }
}
