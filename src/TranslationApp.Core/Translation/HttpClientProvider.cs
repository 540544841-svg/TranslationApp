using System.Net;
using System.Net.Http;

namespace TranslationApp.Core.Translation;

/// <summary>代理作用范围（FR-018）。</summary>
public enum ProxyScope
{
    /// <summary>
    /// 仅国外引擎走代理：Google / Azure / DeepL / AI（13.1.1），
    /// Bing/腾讯/百度等国内可达引擎直连，避免绕路。
    /// 枚举值名保持不变（配置文件里存的是 googleOnly，改了会导致旧配置失效）。
    /// </summary>
    GoogleOnly,

    /// <summary>全部引擎走代理。</summary>
    All,
}

/// <summary>代理配置（原始值，含明文密码；仅在内存中传递，不落盘）。</summary>
public sealed record ProxyOptions(string Host, int Port, string? UserName, string? Password, string Scheme = "http")
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535;

    /// <summary>规范化协议名（仅支持 http / socks5，见 FR-018）。</summary>
    public string NormalizedScheme =>
        string.Equals(Scheme, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";

    /// <summary>
    /// 构造代理。协议名必须显式带上：.NET 6+ 的 WebProxy 支持 <c>socks5://</c>，
    /// 但默认（无协议）会被当作 HTTP 代理，因此 SOCKS5 必须拼出 scheme。
    /// </summary>
    public IWebProxy CreateWebProxy()
    {
        var proxy = new WebProxy($"{NormalizedScheme}://{Host}:{Port}");
        if (!string.IsNullOrEmpty(UserName))
        {
            proxy.Credentials = new NetworkCredential(UserName, Password ?? "");
        }

        return proxy;
    }

    /// <summary>用于缓存键：协议不同即视为不同配置。</summary>
    public string CacheKey => $"{NormalizedScheme}://{Host}:{Port}:{UserName}";
}

/// <summary>
/// 按代理配置提供 HttpClient 的单一入口（FR-018）。
/// 引擎向它取客户端，因此代理设置的变更对两个引擎同时生效，无需重建引擎实例。
/// 客户端按配置缓存：相同配置复用（保持 Cookie 会话与连接池），配置变更时重建。
/// </summary>
public sealed class HttpClientProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ProxyScope, (string Key, HttpClient Client)> _cache = new();

    /// <summary>由宿主注入当前代理配置（返回 null 表示不使用代理）。</summary>
    public Func<ProxyScope, ProxyOptions?>? ProxyResolver { get; set; }

    /// <summary>
    /// 仅供单元测试：注入固定客户端，用于在不联网的前提下验证引擎的请求构造与重试/轮换策略。
    /// 生产路径不会设置该属性。
    /// </summary>
    internal Func<ProxyScope, HttpClient?>? ClientOverrideForTests { get; set; }

    /// <summary>取得指定作用范围下应使用的 HttpClient。</summary>
    public HttpClient Get(ProxyScope scope)
    {
        if (ClientOverrideForTests?.Invoke(scope) is { } overridden)
        {
            return overridden;
        }

        var proxy = ProxyResolver?.Invoke(scope);
        var usable = proxy is { IsValid: true } ? proxy : null;
        var key = usable is null ? "direct" : usable.CacheKey;

        lock (_gate)
        {
            if (_cache.TryGetValue(scope, out var cached))
            {
                if (cached.Key == key)
                {
                    return cached.Client;
                }

                cached.Client.Dispose(); // 代理配置已变更，重建客户端
            }

            var client = TranslationHttpClientFactory.Create(
                usable?.CreateWebProxy());
            _cache[scope] = (key, client);
            return client;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // 注意：字典值的类型是 (string Key, HttpClient Client) 元组，
            // 需要对 KeyValuePair 与元组做两次解构才能取到 HttpClient 本身
            foreach (var (_, (_, client)) in _cache)
            {
                client.Dispose();
            }

            _cache.Clear();
        }
    }
}
