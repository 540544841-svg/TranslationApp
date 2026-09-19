using System.Net;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 官方引擎公共层测试（13.1.1）：超时预算、Key 配置判定、代理归属、错误兜底映射、取消语义。
/// 这些是四家引擎（含后续 Azure/DeepL/AI）共用的契约，改动影响面大，须有回归保护。
/// </summary>
public class OfficialTranslatorBaseTests
{
    // ==================== 超时预算 ====================

    [Fact]
    public void TimeoutFor_ShortText_StaysNearBaseTimeout()
    {
        var translator = CreateBaidu(new AppSettings());

        var budget = translator.TimeoutFor("hello".Length);

        Assert.True(budget >= TimeSpan.FromSeconds(8), $"短文本预算不应低于 8s，实际 {budget}");
        Assert.True(budget < TimeSpan.FromSeconds(8.5), $"短文本预算不应明显超过 8s，实际 {budget}");
    }

    [Fact]
    public void TimeoutFor_LongText_ScalesUpButCappedAt20s()
    {
        var translator = CreateBaidu(new AppSettings());

        var medium = translator.TimeoutFor(450);
        var full = translator.TimeoutFor(900);
        var over = translator.TimeoutFor(5000);

        Assert.True(medium > TimeSpan.FromSeconds(8) && medium < full);
        Assert.Equal(TimeSpan.FromSeconds(20), full);
        Assert.Equal(full, over); // 超过单块上限不再增长
    }

    [Fact]
    public void TimeoutFor_IsSharedByBothOfficialEngines()
    {
        var settings = new AppSettings();

        Assert.Equal(
            CreateBaidu(settings).TimeoutFor(900),
            CreateTencent(settings).TimeoutFor(900));
    }

    // ==================== 取消语义 ====================

    [Fact]
    public async Task TranslateAsync_AlreadyCancelled_ThrowsOperationCanceled()
    {
        // 关闭小窗 / 退出对比即取消在途请求：必须抛 OperationCanceledException（而非翻译失败），
        // 否则界面会把「用户取消」误报成网络错误
        var translator = CreateBaidu(ConfiguredBaidu());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => translator.TranslateAsync("hello", "en", "zh-CN", cts.Token));
    }

    // ==================== 语言码缺失 ====================

    [Fact]
    public async Task TranslateAsync_UnsupportedLanguage_ThrowsEngineErrorInsteadOfPassingItThrough()
    {
        // 13.1.7：映射缺失时必须明确提示「不支持此语言」，不得静默传错码
        var translator = CreateBaidu(ConfiguredBaidu());

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", "en", "th", CancellationToken.None));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
        Assert.Contains("不支持", ex.Message);
    }

    [Fact]
    public async Task TranslateAsync_UnsupportedSourceLanguage_ThrowsEngineError()
    {
        var translator = CreateTencent(ConfiguredTencent());

        var ex = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", "th", "zh-CN", CancellationToken.None));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    // ==================== Key 配置判定 ====================

    [Fact]
    public void BaiduIsConfigured_RequiresBothAppIdAndDecryptableSecret()
    {
        var settings = new AppSettings();
        Assert.False(CreateBaidu(settings).IsConfigured);

        settings.BaiduAppId = "20250101000000001";
        Assert.False(CreateBaidu(settings).IsConfigured); // 只有 APPID，没有密钥

        settings.BaiduAppKeyEncrypted = SecretStore.Protect("secret");
        Assert.True(CreateBaidu(settings).IsConfigured);
    }

    [Fact]
    public void BaiduIsConfigured_RejectsPlaintextSecretValue()
    {
        // 明文值（无 enc: 前缀）解不开，视为未配置：避免残缺配置被当成可用密钥
        var settings = new AppSettings
        {
            BaiduAppId = "20250101000000001",
            BaiduAppKeyEncrypted = "明文密钥",
        };

        Assert.False(CreateBaidu(settings).IsConfigured);
    }

    [Fact]
    public void TencentIsConfigured_RequiresBothSecretIdAndDecryptableSecretKey()
    {
        var settings = new AppSettings();
        Assert.False(CreateTencent(settings).IsConfigured);

        settings.TencentSecretId = "AKIDEXAMPLE1234567890";
        Assert.False(CreateTencent(settings).IsConfigured);

        settings.TencentSecretKeyEncrypted = SecretStore.Protect("secret-key");
        Assert.True(CreateTencent(settings).IsConfigured);
    }

    [Fact]
    public void IsConfigured_IsIndependentPerEngine()
    {
        var settings = ConfiguredBaidu();

        Assert.True(CreateBaidu(settings).IsConfigured);
        Assert.False(CreateTencent(settings).IsConfigured); // 未填腾讯凭据
    }

    // ==================== 代理归属 ====================

    [Fact]
    public void ChineseOfficialEngines_UseAllScopeSoTheyOnlyProxyInAllMode()
    {
        // 13.1.1：腾讯/百度与 Bing 同类（国内可达），仅「全部引擎」模式下走代理
        var settings = new AppSettings();

        Assert.Equal(ProxyScope.All, CreateBaidu(settings).Proxy);
        Assert.Equal(ProxyScope.All, CreateTencent(settings).Proxy);
    }

    // ==================== HTTP 状态兜底映射 ====================

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TranslationErrorType.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, TranslationErrorType.InvalidKey)]
    [InlineData(HttpStatusCode.PaymentRequired, TranslationErrorType.QuotaExceeded)]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorType.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadRequest, TranslationErrorType.Engine)]
    [InlineData(HttpStatusCode.InternalServerError, TranslationErrorType.Engine)]
    [InlineData(HttpStatusCode.BadGateway, TranslationErrorType.Engine)]
    public void FromHttpStatus_MapsToExistingFourCategories(HttpStatusCode status, TranslationErrorType expected)
    {
        var ex = OfficialTranslatorBase.FromHttpStatus(status);

        Assert.Equal(expected, ex.ErrorType);
    }

    [Fact]
    public void NetworkAndTimeoutErrors_AreNetworkCategory()
    {
        var inner = new HttpRequestException("boom");

        Assert.Equal(TranslationErrorType.Network, OfficialTranslatorBase.NetworkError(inner, "网络不可达").ErrorType);
        Assert.Equal(TranslationErrorType.Network, OfficialTranslatorBase.TimeoutError(inner).ErrorType);
    }

    // ==================== 引擎标识 ====================

    [Fact]
    public void EngineIdentifiers_AreStableAndDistinct()
    {
        var settings = new AppSettings();
        var baidu = CreateBaidu(settings);
        var tencent = CreateTencent(settings);

        Assert.Equal("baidu", baidu.Id);
        Assert.Equal("tencent", tencent.Id);
        Assert.NotEqual(baidu.Id, tencent.Id);
        Assert.NotEqual(baidu.Name, tencent.Name);
    }

    private static AppSettings ConfiguredBaidu() => new()
    {
        BaiduAppId = "20250101000000001",
        BaiduAppKeyEncrypted = SecretStore.Protect("Kx7Yq2Ws9Zn4"),
    };

    private static AppSettings ConfiguredTencent() => new()
    {
        TencentSecretId = "AKIDEXAMPLE1234567890",
        TencentSecretKeyEncrypted = SecretStore.Protect("SECRETKEYEXAMPLE0987654321"),
    };

    private static BaiduTranslator CreateBaidu(AppSettings settings) => new(settings, new HttpClientProvider());

    private static TencentTranslator CreateTencent(AppSettings settings) => new(settings, new HttpClientProvider());
}
