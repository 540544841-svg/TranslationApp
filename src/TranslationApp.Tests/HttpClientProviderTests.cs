using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

public sealed class HttpClientProviderTests
{
    [Fact]
    public void Get_同一代理配置复用客户端_仅修改密码时重建()
    {
        var options = new ProxyOptions("127.0.0.1", 7890, "user", "old-password");
        var provider = new HttpClientProvider
        {
            ProxyResolver = _ => options,
        };

        var first = provider.Get(ProxyScope.All);
        var same = provider.Get(ProxyScope.All);
        Assert.Same(first, same);

        options = options with { Password = "new-password" };
        var changed = provider.Get(ProxyScope.All);

        Assert.NotSame(first, changed);
        provider.Dispose();
    }

    [Fact]
    public void CacheKey_不包含明文密码()
    {
        const string password = "super-secret-proxy-password";
        var options = new ProxyOptions("127.0.0.1", 7890, "user", password);

        Assert.DoesNotContain(password, options.CacheKey, StringComparison.Ordinal);
        Assert.NotEqual(
            new ProxyOptions("127.0.0.1", 7890, "user", "other").CacheKey,
            options.CacheKey);
    }
}
