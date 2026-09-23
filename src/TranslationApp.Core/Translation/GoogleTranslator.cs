using System.Net;
using System.Net.Http;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// Google 非官方翻译引擎（FR-006）：零配置，但需网络可访问 Google
/// （部分网络环境不可达，此时请改用 Bing 引擎，或在设置里为国外引擎开启代理）。
/// 接口 https://translate.googleapis.com/translate_a/single（非官方、可能限流/失效）。
///
/// client 参数需轮换重试：实测在国内经代理出网时，<c>client=gtx</c> 会被 Google 以
/// HTTP 429 限流，而 <c>client=dict-chrome-ex</c> 仍返回 200 与正常译文；
/// 两者响应结构一致，因此按序尝试、任一成功即返回，尽量降低“偶发不可用”的概率。
/// </summary>
public sealed class GoogleTranslator : ITranslator
{
    private const string BaseUrl = "https://translate.googleapis.com/translate_a/single";

    /// <summary>按序尝试的客户端标识（首个为实测可用者）。</summary>
    private static readonly string[] Clients = ["dict-chrome-ex", "gtx"];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClientProvider _httpProvider;
    private readonly AppSettings? _settings;

    /// <summary>
    /// settings 为可选注入（FR-028）：只有拿到设置才能按「是否开启自动降级」收紧网络重试；
    /// 未注入时按 FR-006 的默认行为（重试 1 次）工作，因此既有测试与单测无需改动构造方式。
    /// </summary>
    public GoogleTranslator(HttpClientProvider httpProvider, AppSettings? settings = null)
    {
        _httpProvider = httpProvider;
        _settings = settings;
    }

    /// <summary>Google 在部分网络需经代理访问，其作用范围由设置决定（FR-018）。</summary>
    private HttpClient Http => _httpProvider.Get(ProxyScope.GoogleOnly);

    /// <summary>
    /// 网络类失败的重试次数（FR-006 默认 1 次）。
    /// FR-028 / 14.4.1：开启自动降级时降为 **0** —— 有兜底引擎时「快速失败并切换」比「原地重试」
    /// 对用户更有价值，把最坏等待从 2 × 8s = 16s 收到 8s（Bing 已预热，切换后约 200~300 ms 返回）。
    /// **只影响网络类失败**：限流 / 引擎异常仍按 client 轮换重试，本轮换逻辑不受影响。
    /// </summary>
    private int NetworkRetryCount => _settings is { EnableEngineFallback: true } ? 0 : 1;

    public string Id => "google";

    public string Name => "Google（非官方）";

    public bool IsConfigured => true;

    public async Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        TranslationException? lastError = null;
        var networkRetriesLeft = NetworkRetryCount;

        for (var i = 0; i < Clients.Length; i++)
        {
            while (true)
            {
                try
                {
                    return await RequestOnce(text, sourceLanguage, targetLanguage, Clients[i], cancellationToken);
                }
                catch (TranslationException ex)
                {
                    lastError = ex;

                    if (ex.ErrorType == TranslationErrorType.Network)
                    {
                        // 网络不通时换 client 参数没有意义，重试一次即可
                        if (networkRetriesLeft-- > 0)
                        {
                            continue;
                        }

                        throw;
                    }

                    break; // 限流 / 引擎异常：换下一个 client 再试
                }
            }
        }

        throw lastError ?? new TranslationException(TranslationErrorType.Engine, "Google 引擎无可用客户端参数");
    }

    private async Task<TranslationResult> RequestOnce(
        string text, string sourceLanguage, string targetLanguage, string client, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}?client={client}&dt=t&sl={Uri.EscapeDataString(sourceLanguage)}" +
                  $"&tl={Uri.EscapeDataString(targetLanguage)}&q={Uri.EscapeDataString(text)}";

        HttpResponseMessage response;
        // FR-006：请求超时 8s；同时挂上调用方的取消令牌（关窗 / 退出对比即中止在途请求）
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            response = await Http.GetAsync(url, timeout.Token);
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
            if (!response.IsSuccessStatusCode)
            {
                throw ClassifyStatus(response.StatusCode);
            }

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationException(TranslationErrorType.Network, "请求超时", ex);
            }
            var (translated, detected) = GoogleResponseParser.Parse(json);
            return new TranslationResult(translated, detected);
        }
    }

    /// <summary>
    /// 按 HTTP 状态码分类错误：429/403 属限流或被拒（应提示“配额/限流”，而不是笼统的“引擎异常”），
    /// 其余非 2xx 视为引擎异常。
    /// </summary>
    internal static TranslationException ClassifyStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => new TranslationException(
            TranslationErrorType.QuotaExceeded, "引擎返回 HTTP 429（请求过于频繁）"),
        HttpStatusCode.Forbidden => new TranslationException(
            TranslationErrorType.QuotaExceeded, "引擎返回 HTTP 403（请求被拒绝）"),
        _ => new TranslationException(
            TranslationErrorType.Engine, $"引擎返回 HTTP {(int)statusCode}"),
    };
}
