using System.Net;
using System.Text.Json;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// DeepL 引擎测试（13.1.5）：端点自动判定与人工覆盖、鉴权头（不得进 URL/表单）、
/// 省略 source_lang 的自动检测、target_lang 大写、响应解析与错误归类、语言码映射。
/// 「无凭据实测响应」用例的 JSON 取自 build/probe-engines.ps1 的真实返回值（2026-09-13）。
/// </summary>
public class DeepLTranslatorTests
{
    private const string FreeKey = "39a1b2c3-4d5e-6f70-8192-a3b4c5d6e7f8:fx";
    private const string PaidKey = "39a1b2c3-4d5e-6f70-8192-a3b4c5d6e7f8";

    /// <summary>实测返回：无凭据时 HTTP 403 + 该正文（两个端点一致）。</summary>
    private const string ObservedForbiddenBody =
        """{"message":"Missing Authorization header, expected 'Authorization: DeepL-Auth-Key <API key>'. You can find more info in our docs: https://developers.deepl.com/docs/getting-started/auth"}""";

    // ==================== 端点选择 ====================

    [Fact]
    public void ResolveEndpointUrl_FreeKeySuffix_UsesFreeEndpoint()
    {
        // ":fx" 后缀 = 免费版 Key（官方约定），即使人工覆盖要求付费端点也以后缀为准
        Assert.Equal(DeepLTranslator.FreeEndpointUrl, DeepLTranslator.ResolveEndpointUrl(FreeKey, true));
        Assert.Equal(DeepLTranslator.FreeEndpointUrl, DeepLTranslator.ResolveEndpointUrl(FreeKey, false));
    }

    [Fact]
    public void ResolveEndpointUrl_PaidKeyWithoutOverride_UsesPaidEndpoint()
    {
        // 无 ":fx" 且未人工覆盖（设置页会按后缀把该字段同步为 false）→ 付费端点
        Assert.Equal(DeepLTranslator.PaidEndpointUrl, DeepLTranslator.ResolveEndpointUrl(PaidKey, false));
    }

    [Fact]
    public void ResolveEndpointUrl_ManualOverrideWinsForPaidKey()
    {
        // 人工覆盖生效：无 ":fx" 但开关指向免费端点
        Assert.Equal(DeepLTranslator.FreeEndpointUrl, DeepLTranslator.ResolveEndpointUrl(PaidKey, true));
    }

    [Theory]
    [InlineData("abc:fx", true)]
    [InlineData("abc:FX", true)]
    [InlineData("abc:fx  ", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void KeyUsesFreeEndpoint_DetectsSuffixCaseInsensitively(string? key, bool expected)
    {
        Assert.Equal(expected, DeepLTranslator.KeyUsesFreeEndpoint(key));
    }

    [Fact]
    public void CreateRequest_RoutesToEndpointDecidedByKeySuffix()
    {
        using var free = CreateTranslator(FreeKey).CreateRequest("hello", "en", "ZH");
        using var paid = CreateTranslator(PaidKey, useFreeEndpoint: false).CreateRequest("hello", "en", "ZH");

        Assert.Equal("https://api-free.deepl.com/v2/translate", free.RequestUri!.ToString());
        Assert.Equal("https://api.deepl.com/v2/translate", paid.RequestUri!.ToString());
    }

    // ==================== 鉴权 ====================

    [Fact]
    public void CreateRequest_UsesAuthorizationHeaderAndKeepsKeyOutOfUrl()
    {
        // 13.1.5：不要用 URL/表单里的 auth_key 参数，避免密钥进入 URL 与日志
        using var request = CreateTranslator(PaidKey, useFreeEndpoint: false).CreateRequest("hello", "en", "ZH");

        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal($"DeepL-Auth-Key {PaidKey}", Assert.Single(values));
        Assert.DoesNotContain(PaidKey, request.RequestUri!.ToString());
        Assert.DoesNotContain("auth_key", request.RequestUri.ToString());
    }

    // ==================== 请求体 ====================

    [Fact]
    public void BuildRequestBody_AutoSource_OmitsSourceLang()
    {
        // 13.1.5（对第 5 章原始表述的更正）：省略 source_lang 时 DeepL 自行检测源语言
        var body = DeepLTranslator.BuildRequestBody("hello", "auto", "ZH");

        Assert.DoesNotContain("source_lang", body);
        Assert.Contains("\"target_lang\":\"ZH\"", body);
        Assert.Contains("\"text\":[\"hello\"]", body);
    }

    [Fact]
    public void BuildRequestBody_ExplicitSource_SendsSourceLang()
    {
        var body = DeepLTranslator.BuildRequestBody("hello", "EN", "ZH");

        Assert.Contains("\"source_lang\":\"EN\"", body);
    }

    [Fact]
    public void BuildRequestBody_TargetLanguageIsUpperCased()
    {
        var body = DeepLTranslator.BuildRequestBody("hello", "auto", DeepLTranslator.ToDeepLCode("zh-TW")!);

        Assert.Contains("\"target_lang\":\"ZH-HANT\"", body);
    }

    [Fact]
    public void BuildRequestBody_IsValidJsonWithSingleTextElement()
    {
        using var document = JsonDocument.Parse(DeepLTranslator.BuildRequestBody("你好", "auto", "ZH"));

        var text = document.RootElement.GetProperty("text");
        Assert.Equal(JsonValueKind.Array, text.ValueKind);
        Assert.Equal("你好", Assert.Single(text.EnumerateArray()).GetString());
    }

    // ==================== 响应解析 ====================

    [Fact]
    public void Parse_Success_ReturnsTextAndDetectedSource()
    {
        // 自动检测时 DeepL 用 detected_source_language 回传（大写）
        var (text, detected) = DeepLResponseParser.Parse(
            """{"translations":[{"detected_source_language":"EN","text":"你好"}]}""");

        Assert.Equal("你好", text);
        Assert.Equal("EN", detected);
        Assert.Equal("en", DeepLTranslator.FromDeepLCode(detected!)); // 回填为内部码
    }

    [Fact]
    public void Parse_MissingTranslations_ThrowsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(() => DeepLResponseParser.Parse("""{"translations":[]}"""));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void Parse_EmptyText_ThrowsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(
            () => DeepLResponseParser.Parse("""{"translations":[{"text":""}]}"""));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Fact]
    public void TryReadMessage_ReadsObservedForbiddenBody()
    {
        Assert.Contains("Missing Authorization header", DeepLResponseParser.TryReadMessage(ObservedForbiddenBody));
        Assert.Null(DeepLResponseParser.TryReadMessage("""{"translations":[]}"""));
        Assert.Null(DeepLResponseParser.TryReadMessage("not json"));
    }

    // ==================== 错误映射 ====================

    [Theory]
    [InlineData(400, TranslationErrorType.Engine)]          // 语言码/参数错误
    [InlineData(401, TranslationErrorType.InvalidKey)]
    [InlineData(403, TranslationErrorType.InvalidKey)]      // 鉴权失败（实测值）
    [InlineData(413, TranslationErrorType.Engine)]          // 请求过大（实现已分块）
    [InlineData(429, TranslationErrorType.QuotaExceeded)]   // 限流
    [InlineData(456, TranslationErrorType.QuotaExceeded)]   // 额度用尽（非标准状态码）
    [InlineData(500, TranslationErrorType.Engine)]
    [InlineData(503, TranslationErrorType.Engine)]
    public void MapStatus_MatchesDocumentTable(int status, TranslationErrorType expected)
    {
        Assert.Equal(expected, DeepLResponseParser.MapStatus((HttpStatusCode)status));
    }

    [Fact]
    public void CreateError_ObservedForbidden_IsInvalidKeyAndKeepsDetail()
    {
        var ex = DeepLResponseParser.CreateError(
            HttpStatusCode.Forbidden, DeepLResponseParser.TryReadMessage(ObservedForbiddenBody));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains("DeepL 密钥无效或未授权", ex.Message);
        Assert.Contains("Missing Authorization header", ex.Message);
        Assert.DoesNotContain(PaidKey, ex.Message); // 文案不得回显 Key
    }

    [Fact]
    public void CreateError_QuotaExceededStatus_IsClassified()
    {
        var ex = DeepLResponseParser.CreateError(
            (HttpStatusCode)DeepLResponseParser.QuotaExceededStatus, "Quota exceeded");

        Assert.Equal(TranslationErrorType.QuotaExceeded, ex.ErrorType);
        Assert.Contains("额度用尽或触发限流", ex.Message);
    }

    // ==================== 语言码映射 ====================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("zh-CN", "ZH")]
    [InlineData("zh-TW", "ZH-HANT")]
    [InlineData("en", "EN")]
    [InlineData("ja", "JA")]
    [InlineData("ko", "KO")]
    [InlineData("fr", "FR")]
    [InlineData("de", "DE")]
    [InlineData("ru", "RU")]
    [InlineData("es", "ES")]
    public void ToDeepLCode_IsUppercaseAndMatchesTable(string internalCode, string expected)
    {
        var code = DeepLTranslator.ToDeepLCode(internalCode);

        Assert.Equal(expected, code);
        if (internalCode != TranslationLanguages.AutoCode)
        {
            // 13.1.7：DeepL 语言码必须大写（auto 是「省略 source_lang」的哨兵值，不参与大写约定）
            Assert.Equal(code, code!.ToUpperInvariant());
        }
    }

    [Fact]
    public void ToDeepLCode_UnsupportedLanguage_ReturnsNull()
    {
        Assert.Null(DeepLTranslator.ToDeepLCode("th"));
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("JA", "ja")]
    [InlineData("ZH", "zh-CN")]
    [InlineData("ZH-HANS", "zh-CN")]
    [InlineData("ZH-HANT", "zh-TW")]
    public void FromDeepLCode_MapsBackToInternal(string engineCode, string expected)
    {
        Assert.Equal(expected, DeepLTranslator.FromDeepLCode(engineCode));
    }

    // ==================== 配置判定与代理归属 ====================

    [Fact]
    public void IsConfigured_RequiresDecryptableSecret()
    {
        Assert.False(CreateTranslator("").IsConfigured);
        Assert.True(CreateTranslator(FreeKey).IsConfigured);

        // 手工写入的明文/非法密文（无 enc: 前缀）解密失败 → 视为未配置
        var tampered = new AppSettings { DeepLApiKeyEncrypted = "plaintext-key" };
        Assert.False(new DeepLTranslator(tampered, new HttpClientProvider()).IsConfigured);
    }

    [Fact]
    public void ProxyScope_IsForeignEngine()
    {
        // 13.1.1：DeepL 归入「仅国外引擎」作用域
        Assert.Equal(ProxyScope.GoogleOnly, CreateTranslator(PaidKey).Proxy);
    }

    [Fact]
    public void IdAndName_AreStable()
    {
        var translator = CreateTranslator(PaidKey);

        Assert.Equal("deepl", translator.Id);
        Assert.Equal("DeepL", translator.Name);
    }

    // ==================== 可达性判定（13.9） ====================

    [Fact]
    public void Reachability_AnyHttpAnswerMeansReachable()
    {
        Assert.Equal(EndpointReachability.Reachable, EngineReachability.Classify(200));
        Assert.Equal(EndpointReachability.Reachable, EngineReachability.Classify(403)); // 实测返回值
        Assert.Equal(EndpointReachability.Unreachable, EngineReachability.Classify(null));
    }

    private static DeepLTranslator CreateTranslator(string plainKey, bool useFreeEndpoint = false)
    {
        var settings = new AppSettings
        {
            DeepLApiKeyEncrypted = SecretStore.Protect(plainKey),
            DeepLUseFreeEndpoint = useFreeEndpoint,
        };

        return new DeepLTranslator(settings, new HttpClientProvider());
    }
}
