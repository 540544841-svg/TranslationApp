using System.Net.Http;
using System.Text;

namespace TranslationApp.Core.Translation;

/// <summary>端点可达性（13.9）。</summary>
public enum EndpointReachability
{
    /// <summary>尚未探测。</summary>
    Unknown,

    /// <summary>端点可达（拿到任意 HTTP 应答，含 401/403 这类「无凭据」应答）。</summary>
    Reachable,

    /// <summary>不可达（DNS 失败 / 连接失败 / 超时）。</summary>
    Unreachable,
}

/// <summary>
/// 引擎端点可达性探测（13.9：Azure / DeepL 需先确认当前网络能否直连）。
/// 判定标准：**只要收到 HTTP 应答即视为可达**（401/403/429 都是服务端在正常应答，说明域名与链路通），
/// 传输层失败才算不可达。探测请求不带任何 Key，不消耗额度。
/// </summary>
public static class EngineReachability
{
    /// <summary>探测请求的超时（不可达时不要拖住设置页）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    /// <summary>分类：有状态码 → 可达；无状态码（传输层失败）→ 不可达。抽成纯函数以便单测。</summary>
    public static EndpointReachability Classify(int? statusCode) =>
        statusCode.HasValue ? EndpointReachability.Reachable : EndpointReachability.Unreachable;

    /// <summary>
    /// 发一次无凭据的探测请求。<paramref name="client"/> 由调用方按引擎的代理作用范围提供，
    /// 保证探测结果与实际翻译走同一条链路（代理开启时不会误报不可达）。
    /// </summary>
    public static async Task<EndpointReachability> ProbeAsync(
        HttpClient client,
        Uri url,
        string? jsonBody = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cts.Token);
            return Classify((int)response.StatusCode);
        }
        catch (Exception)
        {
            // 探测失败不是错误：只表示当前网络下不可直连（DNS/连接/超时/被重置）
            return Classify(null);
        }
    }
}
