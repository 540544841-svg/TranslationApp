using System.Net;
using System.Net.Http;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// Google 引擎的 client 参数轮换与状态码分类测试。
/// 背景（2026-09-13 实测）：国内经代理出网时 <c>client=gtx</c> 被 Google 以 HTTP 429 限流，
/// 而 <c>client=dict-chrome-ex</c> 正常返回 200；因此引擎按序尝试多个 client。
/// </summary>
public sealed class GoogleTranslatorTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, TranslationErrorType.QuotaExceeded)]
    [InlineData(HttpStatusCode.Forbidden, TranslationErrorType.QuotaExceeded)]
    [InlineData(HttpStatusCode.BadRequest, TranslationErrorType.Engine)]
    [InlineData(HttpStatusCode.InternalServerError, TranslationErrorType.Engine)]
    public void ClassifyStatus_MapsThrottlingToQuotaAndOthersToEngine(
        HttpStatusCode status, TranslationErrorType expected)
    {
        var exception = GoogleTranslator.ClassifyStatus(status);

        Assert.Equal(expected, exception.ErrorType);
    }

    [Fact]
    public void ClassifyStatus_ThrottlingMessage_Mentions429()
    {
        var exception = GoogleTranslator.ClassifyStatus(HttpStatusCode.TooManyRequests);

        Assert.Contains("429", exception.Message);
    }

    /// <summary>把「按 client 依次尝试」的行为锁死：首个被限流时应改用备用 client 并成功返回。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;
        public List<string> RequestUris { get; } = [];

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestUris.Add(uri);
            return Task.FromResult(_respond(uri));
        }
    }

    private static HttpClientProvider ProviderFor(StubHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var provider = new HttpClientProvider();
        // 让 provider 返回我们注入的客户端，从而在不联网的情况下验证请求构造与轮换逻辑
        provider.ClientOverrideForTests = _ => client;
        return provider;
    }

    [Fact]
    public async Task TranslateAsync_PrimaryClientIsTheWorkingOne()
    {
        // 锁定「首选 client 是实测可用的那个」：dict-chrome-ex 必须排在 gtx 之前，
        // 否则每次翻译都会先撞一次 429 再轮换，白白多一次往返。
        const string okBody = """[[["你好","hello",null,null,10]],null,"en"]""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(okBody),
        });

        var translator = new GoogleTranslator(ProviderFor(handler));

        await translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN");

        Assert.Single(handler.RequestUris);
        Assert.Contains("client=dict-chrome-ex", handler.RequestUris[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranslateAsync_WhenPrimaryClientThrottled_RotatesToAlternateAndSucceeds()
    {
        // 真正的轮换验证：首选 client 被限流，必须改用备用 client 并成功
        const string okBody = """[[["你好","hello",null,null,10]],null,"en"]""";
        var handler = new StubHandler(uri =>
            uri.Contains("client=dict-chrome-ex", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(okBody) });

        var translator = new GoogleTranslator(ProviderFor(handler));

        var result = await translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN");

        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("en", result.DetectedSourceLanguage);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("client=dict-chrome-ex", handler.RequestUris[0], StringComparison.Ordinal);
        Assert.Contains("client=gtx", handler.RequestUris[1], StringComparison.Ordinal); // 已轮换到备用 client
    }

    [Fact]
    public async Task TranslateAsync_WhenAllClientsThrottled_ReportsQuotaExceeded()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var translator = new GoogleTranslator(ProviderFor(handler));

        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN"));

        Assert.Equal(TranslationErrorType.QuotaExceeded, exception.ErrorType);
        Assert.Equal(2, handler.RequestUris.Count); // 两个 client 各试一次
    }

    [Fact]
    public async Task TranslateAsync_ClientRotationAlsoAppliesToEngineErrors()
    {
        // 首个 client 报服务端错误时也应换备用 client（不局限在 429）
        const string okBody = """[[["你好","hello",null,null,10]],null,"en"]""";
        var handler = new StubHandler(uri =>
            uri.Contains("client=dict-chrome-ex", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(okBody) });

        var translator = new GoogleTranslator(ProviderFor(handler));

        var result = await translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN");

        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task TranslateAsync_NetworkFailure_RetriesOnceThenReportsNetworkError()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        var translator = new GoogleTranslator(ProviderFor(handler));

        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN"));

        Assert.Equal(TranslationErrorType.Network, exception.ErrorType);
        // 网络不通时换 client 参数没有意义：只重试 1 次即停（2 次请求）
        Assert.Equal(2, handler.RequestUris.Count);
    }

    // ==================== FR-028：开启降级后网络重试降为 0（14.4.1 超时预算收紧） ====================

    [Fact]
    public async Task TranslateAsync_开启引擎降级时_网络失败不重试()
    {
        // 有兜底引擎时「快速失败并切换」比「原地重试」对用户更有价值：最坏等待 8s 而非 16s
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        var translator = new GoogleTranslator(ProviderFor(handler), new AppSettings { EnableEngineFallback = true });

        var exception = await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN"));

        Assert.Equal(TranslationErrorType.Network, exception.ErrorType);
        Assert.Single(handler.RequestUris); // 只发一次，立刻把控制权交给降级路径
    }

    [Fact]
    public async Task TranslateAsync_关闭引擎降级时_网络失败仍重试一次()
    {
        // 无兜底引擎时保留 FR-006 的原有重试行为（瞬时抖动仍有机会自愈）
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        var translator = new GoogleTranslator(ProviderFor(handler), new AppSettings { EnableEngineFallback = false });

        await Assert.ThrowsAsync<TranslationException>(
            () => translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN"));

        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task TranslateAsync_开启降级不影响client轮换()
    {
        // 收紧的只有「网络类失败的重试」：限流 / 引擎异常仍按 client 轮换，否则 429 的自愈能力会丢。
        // 这里让首个 client（dict-chrome-ex）被 429、备用 client（gtx）正常返回，真正验证一次轮换。
        const string okBody = """[[["你好","hello",null,null,10]],null,"en"]""";
        var handler = new StubHandler(uri =>
            uri.Contains("client=gtx", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(okBody) }
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var translator = new GoogleTranslator(ProviderFor(handler), new AppSettings { EnableEngineFallback = true });

        var result = await translator.TranslateAsync("hello", TranslationLanguages.AutoCode, "zh-CN");

        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Contains("client=gtx", handler.RequestUris[1], StringComparison.Ordinal);
    }
}
