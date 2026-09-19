using System.Runtime.Versioning;
using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>敏感信息加密测试（FR-010/018）：DPAPI 往返与格式识别。</summary>
[SupportedOSPlatform("windows")]
public class SecretStoreTests
{
    [Fact]
    public void ProtectThenUnprotect_ReturnsOriginal()
    {
        const string secret = "p@ssw0rd-代理密码-123";

        var encrypted = SecretStore.Protect(secret);

        Assert.Equal(secret, SecretStore.Unprotect(encrypted));
    }

    [Fact]
    public void Protect_DoesNotLeakPlainText()
    {
        const string secret = "my-secret-token";

        var encrypted = SecretStore.Protect(secret);

        Assert.DoesNotContain(secret, encrypted);
        Assert.StartsWith("enc:", encrypted);
        Assert.True(SecretStore.IsEncrypted(encrypted));
    }

    [Fact]
    public void Protect_IsNotDeterministic()
    {
        // DPAPI 每次加密产生不同密文（含随机盐），因此不能用密文比较判断密码是否变化
        var first = SecretStore.Protect("same-value");
        var second = SecretStore.Protect("same-value");

        Assert.NotEqual(first, second);
        Assert.Equal(SecretStore.Unprotect(first), SecretStore.Unprotect(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Protect_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal("", SecretStore.Protect(input));
    }

    [Fact]
    public void Unprotect_PlainTextValue_ReturnsNull()
    {
        // 非本程序写入的明文（例如用户手改过配置文件）不应被误当作密文
        Assert.Null(SecretStore.Unprotect("plain-text-password"));
    }

    [Fact]
    public void Unprotect_CorruptedCipherText_ReturnsNull()
    {
        Assert.Null(SecretStore.Unprotect("enc:这不是有效的Base64!!!"));
    }

    [Fact]
    public void IsEncrypted_DetectsFormat()
    {
        Assert.True(SecretStore.IsEncrypted("enc:abc"));
        Assert.False(SecretStore.IsEncrypted("abc"));
        Assert.False(SecretStore.IsEncrypted(null));
        Assert.False(SecretStore.IsEncrypted(""));
    }
}
