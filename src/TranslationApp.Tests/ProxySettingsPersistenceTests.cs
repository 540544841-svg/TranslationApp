using System.Runtime.Versioning;
using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 敏感配置落盘测试（FR-010/018 验收标准：配置文件中看不到明文 Key）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProxySettingsPersistenceTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"ta-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Save_WithProxyPassword_StoresCipherTextOnly()
    {
        const string plainPassword = "super-secret-proxy-password";
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.ProxyEnabled = true;
        settings.ProxyMode = "all";
        settings.ProxyHost = "127.0.0.1";
        settings.ProxyPort = 7890;
        settings.ProxyUserName = "proxyuser";
        settings.ProxyPasswordEncrypted = SecretStore.Protect(plainPassword);
        store.Save(settings);

        // 文件内容中不得出现明文密码（FR-010 验收标准）
        var fileContent = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain(plainPassword, fileContent);
        Assert.Contains("enc:", fileContent);

        // 重新载入后仍可解出原密码，且其余代理设置保持不变
        var reloaded = store.Load();
        Assert.Equal(plainPassword, SecretStore.Unprotect(reloaded.ProxyPasswordEncrypted));
        Assert.True(reloaded.ProxyEnabled);
        Assert.Equal("all", reloaded.ProxyMode);
        Assert.Equal("127.0.0.1", reloaded.ProxyHost);
        Assert.Equal(7890, reloaded.ProxyPort);
        Assert.Equal("proxyuser", reloaded.ProxyUserName);
    }

    [Fact]
    public void Save_AndReload_PreservesPhase3Settings()
    {
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.ClipboardMonitorEnabled = true;
        settings.AutoSpeakAfterSelect = true;
        settings.Theme = "dark";
        store.Save(settings);

        var reloaded = store.Load();

        Assert.True(reloaded.ClipboardMonitorEnabled);
        Assert.True(reloaded.AutoSpeakAfterSelect);
        Assert.Equal("dark", reloaded.Theme);
    }

    [Fact]
    public void Save_WithOfficialEngineSecrets_StoresCipherTextOnly()
    {
        // FR-024 / AC 3：腾讯 SecretKey 与百度密钥也必须密文落盘，配置文件中不得出现明文
        const string tencentKey = "tencent-secret-key-plaintext";
        const string baiduKey = "baidu-app-key-plaintext";
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.TencentSecretId = "AKIDEXAMPLE1234567890";
        settings.TencentSecretKeyEncrypted = SecretStore.Protect(tencentKey);
        settings.TencentRegion = "ap-shanghai";
        settings.BaiduAppId = "20250101000000001";
        settings.BaiduAppKeyEncrypted = SecretStore.Protect(baiduKey);
        store.Save(settings);

        var fileContent = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain(tencentKey, fileContent);
        Assert.DoesNotContain(baiduKey, fileContent);

        // 非密钥标识与区域按文档明文存储（便于排障），密钥可解回原文
        Assert.Contains("AKIDEXAMPLE1234567890", fileContent);
        Assert.Contains("20250101000000001", fileContent);

        var reloaded = store.Load();
        Assert.Equal(tencentKey, SecretStore.Unprotect(reloaded.TencentSecretKeyEncrypted));
        Assert.Equal(baiduKey, SecretStore.Unprotect(reloaded.BaiduAppKeyEncrypted));
        Assert.Equal("ap-shanghai", reloaded.TencentRegion);
    }

    [Fact]
    public void Save_WithAzureAndDeepLSecrets_StoresCipherTextOnly()
    {
        // FR-024 / AC 3：Azure 与 DeepL 的 Key 同样必须密文落盘
        const string azureKey = "azure-subscription-key-plaintext";
        const string deeplKey = "deepl-api-key-plaintext:fx";
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.AzureSubscriptionKeyEncrypted = SecretStore.Protect(azureKey);
        settings.AzureRegion = "eastasia";
        settings.DeepLApiKeyEncrypted = SecretStore.Protect(deeplKey);
        settings.DeepLUseFreeEndpoint = true;
        store.Save(settings);

        var fileContent = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain(azureKey, fileContent);
        Assert.DoesNotContain(deeplKey, fileContent);
        Assert.Contains("enc:", fileContent);
        // 非密钥的区域按文档明文存储（便于排障）
        Assert.Contains("eastasia", fileContent);
        // 13.7：字段名必须与文档一致
        Assert.Contains("AzureSubscriptionKeyEncrypted", fileContent);
        Assert.Contains("DeepLApiKeyEncrypted", fileContent);
        Assert.Contains("DeepLUseFreeEndpoint", fileContent);

        var reloaded = store.Load();
        Assert.Equal(azureKey, SecretStore.Unprotect(reloaded.AzureSubscriptionKeyEncrypted));
        Assert.Equal(deeplKey, SecretStore.Unprotect(reloaded.DeepLApiKeyEncrypted));
        Assert.Equal("eastasia", reloaded.AzureRegion);
        Assert.True(reloaded.DeepLUseFreeEndpoint);
    }

    [Fact]
    public void Save_DeepLUseFreeEndpointFalse_RoundTrips()
    {
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.DeepLApiKeyEncrypted = SecretStore.Protect("paid-key");
        settings.DeepLUseFreeEndpoint = false;
        store.Save(settings);

        Assert.False(store.Load().DeepLUseFreeEndpoint);
    }

    [Fact]
    public void Load_LegacySettingsWithoutOfficialEngineFields_UsesDefaults()
    {
        // 13.7：旧配置文件没有新字段时必须能读，且取到文档规定的默认值
        File.WriteAllText(_settingsPath, """{"Engine":"bing","TargetLanguage":"zh-CN"}""");
        var settings = new JsonSettingsStore(_settingsPath).Load();

        Assert.Equal("", settings.TencentSecretId);
        Assert.Equal("", settings.TencentSecretKeyEncrypted);
        Assert.Equal("ap-guangzhou", settings.TencentRegion);
        Assert.Equal("", settings.BaiduAppId);
        Assert.Equal("", settings.BaiduAppKeyEncrypted);
        Assert.Equal("", settings.AzureSubscriptionKeyEncrypted);
        Assert.Equal("", settings.AzureRegion);
        Assert.Equal("", settings.DeepLApiKeyEncrypted);
        Assert.True(settings.DeepLUseFreeEndpoint);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_settingsPath);
        }
        catch
        {
            // 清理失败不影响结论
        }
    }
}
