using System.Net;
using System.Net.Http;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 翻译引擎共用的 HttpClient 工厂。
/// 必须使用真实浏览器 UA：Bing 接口对爬虫特征 UA 直接返回 401（实测极简 UA 失败、完整 Chrome UA 成功）；
/// 同时启用 Cookie 容器，因为 Bing 的页面令牌与后续请求需在同一会话内。
/// </summary>
public static class TranslationHttpClientFactory
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// 创建引擎专用 HttpClient。超时设为兜底上限（60s），实际每请求预算由各引擎按文本长度用
    /// CancellationToken 精确控制：短文本 8s（FR-006），长文本按比例放宽（服务端翻译耗时更长）。
    /// </summary>
    /// <param name="proxy">代理（FR-018）；null 表示直连。</param>
    /// <param name="timeout">兜底超时，默认 60s。</param>
    public static HttpClient Create(IWebProxy? proxy = null, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        if (proxy is not null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }
}
