using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TranslationApp.Core.Translation;

/// <summary>
/// Bing 翻译非官方引擎（FR-006 默认引擎）：零配置开箱即用，国内网络可直连。
/// 流程：GET cn.bing.com/translator 页面提取 IG / IID / AbusePrevention 令牌（缓存约 8 分钟），
/// 再 POST ttranslatev3 表单。响应形如
/// [{"translations":[{"text":"你好","to":"zh-Hans"}],"detectedLanguage":{"language":"en"}}]。
/// 源语言用 auto-detect 由服务端识别并回传 detectedLanguage。
/// </summary>
public sealed class BingTranslator : ITranslator
{
    private const string TranslatorPage = "https://cn.bing.com/translator";
    private const string TranslateApi = "https://cn.bing.com/ttranslatev3?isVertical=1";
    private const string AutoDetect = "auto-detect";

    /// <summary>单次请求文本上限（留余量，按句子边界切块）。</summary>
    private const int MaxChunkLength = 900;

    /// <summary>短文本请求预算（FR-006）。</summary>
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>长文本额外预算：900 字符约 20s（服务端翻译耗时随文本增长）。</summary>
    private static readonly TimeSpan TimeoutPerFullChunk = TimeSpan.FromSeconds(12);

    /// <summary>页面取令牌的预算：页面约 600KB，8s 在慢速网络下会超时（实测出现过），放宽并允许重试一次。</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(20);

    private const int PageFetchAttempts = 2;

    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(8);

    private static readonly Regex IgRegex = new("IG:\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex IidRegex = new("data-iid=\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex AbuseRegex = new(
        "params_AbusePreventionHelper\\s*=\\s*\\[([0-9]+),\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly HttpClientProvider _httpProvider;
    private readonly SemaphoreSlim _credentialLock = new(1, 1);
    private Credentials? _credentials;

    public BingTranslator(HttpClientProvider httpProvider) => _httpProvider = httpProvider;

    /// <summary>Bing 在国内网络可直连，仅当代理设置为「全局」时才走代理（FR-018）。</summary>
    private HttpClient Http => _httpProvider.Get(ProxyScope.All);

    public string Id => "bing";

    public string Name => "Bing（非官方，零配置）";

    public bool IsConfigured => true;

    /// <summary>
    /// 预热令牌（启动时后台调用）：Bing 首次翻译需先抓取翻译页提取令牌，约多耗数秒；
    /// 提前取好可让用户第一次按热键就能秒开。失败静默（下次翻译会自行重试）。
    /// </summary>
    public async Task WarmUpAsync()
    {
        try
        {
            await GetCredentialsAsync(forceRefresh: false, CancellationToken.None);
        }
        catch (TranslationException)
        {
            // 预热失败不影响使用：真正翻译时会再取一次并给出明确错误提示
        }
    }

    private sealed record Credentials(string Ig, string Iid, string Key, string Token, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var target = ToBingCode(targetLanguage);
        var source = sourceLanguage == TranslationLanguages.AutoCode ? AutoDetect : ToBingCode(sourceLanguage);

        var builder = new StringBuilder();
        string? detected = null;

        foreach (var chunk in SplitIntoChunks(text, MaxChunkLength))
        {
            var (translated, detectedInChunk) = await WithCredentialRetryAsync(chunk, source, target, cancellationToken);
            builder.Append(translated);
            detected ??= detectedInChunk;
        }

        return new TranslationResult(builder.ToString(), detected is null ? null : FromBingCode(detected));
    }

    /// <summary>令牌过期/失效时刷新一次并重试（Bing 令牌有效期短，属正常情况）。</summary>
    private async Task<(string Text, string? Detected)> WithCredentialRetryAsync(
        string text, string source, string target, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var credentials = await GetCredentialsAsync(forceRefresh: attempt > 1, cancellationToken);
            try
            {
                return await RequestOnceAsync(text, source, target, credentials, cancellationToken);
            }
            catch (TranslationException ex)
                when (ex.ErrorType == TranslationErrorType.Engine && attempt == 1)
            {
                InvalidateCredentials();
            }
        }
    }

    /// <summary>按文本长度决定请求预算：短文本 8s，长文本按比例放宽（上限 20s）。</summary>
    internal static TimeSpan TimeoutFor(string text)
    {
        var extra = TimeoutPerFullChunk * Math.Min(1.0, (double)text.Length / MaxChunkLength);
        return BaseTimeout + extra;
    }

    private async Task<(string Text, string? Detected)> RequestOnceAsync(
        string text, string source, string target, Credentials credentials, CancellationToken cancellationToken)
    {
        var url = $"{TranslateApi}&&IG={Uri.EscapeDataString(credentials.Ig)}&IID={Uri.EscapeDataString(credentials.Iid)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fromLang"] = source,
                ["text"] = text,
                ["to"] = target,
                ["token"] = credentials.Token,
                ["key"] = credentials.Key,
            }),
        };
        // Bing 校验来源，缺失 Referer/Origin 会返回 401
        request.Headers.Referrer = new Uri(TranslatorPage);
        request.Headers.TryAddWithoutValidation("Origin", "https://cn.bing.com");

        // 每次尝试都有独立预算；同时挂上调用方的取消令牌
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutFor(text));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException(TranslationErrorType.Network, "网络不可达", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationException(TranslationErrorType.Network, "请求超时", ex);
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync();
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                // 令牌失效：交给上层刷新后重试
                throw new TranslationException(TranslationErrorType.Engine, "Bing 令牌失效");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationException(TranslationErrorType.Engine, $"引擎返回 HTTP {(int)response.StatusCode}");
            }

            return BingResponseParser.Parse(json);
        }
    }

    private async Task<Credentials> GetCredentialsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _credentials is { IsExpired: false } cached)
        {
            return cached;
        }

        await _credentialLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _credentials is { IsExpired: false } refreshed)
            {
                return refreshed;
            }

            string html;
            TranslationException? lastError = null;
            for (var attempt = 1; attempt <= PageFetchAttempts; attempt++)
            {
                using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pageTimeout.CancelAfter(PageTimeout);
                try
                {
                    html = await Http.GetStringAsync(TranslatorPage, pageTimeout.Token);
                    _credentials = Parse(html);
                    return _credentials;
                }
                catch (HttpRequestException ex)
                {
                    lastError = new TranslationException(TranslationErrorType.Network, "无法访问 Bing 翻译页面", ex);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = new TranslationException(TranslationErrorType.Network, "访问 Bing 翻译页面超时", ex);
                }
            }

            throw lastError!;
        }
        finally
        {
            _credentialLock.Release();
        }
    }

    private Credentials Parse(string html)
    {
        var (ig, iid, key, token) = ParseCredentials(html);
        return new Credentials(ig, iid, key, token, DateTimeOffset.UtcNow.Add(CredentialLifetime));
    }

    /// <summary>从翻译页 HTML 提取令牌（提取失败视为引擎异常，非网络问题）。</summary>
    internal static (string Ig, string Iid, string Key, string Token) ParseCredentials(string html)
    {
        var ig = IgRegex.Match(html).Groups[1].Value;
        var iid = IidRegex.Match(html).Groups[1].Value;
        var abuse = AbuseRegex.Match(html);
        var key = abuse.Groups[1].Value;
        var token = abuse.Groups[2].Value;

        if (string.IsNullOrEmpty(ig) || string.IsNullOrEmpty(iid)
            || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(token))
        {
            throw new TranslationException(TranslationErrorType.Engine, "Bing 页面令牌解析失败（页面结构可能已变更）");
        }

        return (ig, iid, key, token);
    }

    private void InvalidateCredentials() => _credentials = null;

    /// <summary>按段落/句末标点切块，避免从词中间截断（转发到共享工具，保持既有测试语义）。</summary>
    internal static IEnumerable<string> SplitIntoChunks(string text, int maxLength) =>
        TextChunker.Split(text, maxLength);

    /// <summary>内部语言码（= Google 码）→ Bing 语言码。</summary>
    internal static string ToBingCode(string code) => code switch
    {
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        _ => code,
    };

    /// <summary>Bing 语言码 → 内部语言码。</summary>
    internal static string FromBingCode(string code) => code switch
    {
        "zh-Hans" => "zh-CN",
        "zh-Hant" => "zh-TW",
        _ => code,
    };
}
