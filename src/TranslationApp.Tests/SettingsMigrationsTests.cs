using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>配置结构迁移：版本 1 → 2 的小窗改版尺寸迁移。</summary>
public class SettingsMigrationsTests
{
    [Fact]
    public void Upgrade_旧版出厂尺寸_迁移到设计稿小窗尺寸()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            QuickWindowWidth = 420,
            QuickWindowHeight = 320,
        };

        var upgraded = SettingsMigrations.Upgrade(settings);

        Assert.Equal(SettingsMigrations.CurrentSchemaVersion, upgraded.SchemaVersion);
        Assert.Equal(Core.Layout.WindowSizePolicy.DefaultWidthDip, upgraded.QuickWindowWidth);
        Assert.Equal(Core.Layout.WindowSizePolicy.DefaultHeightDip, upgraded.QuickWindowHeight);
    }

    [Fact]
    public void Upgrade_用户改过尺寸_原样保留()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            QuickWindowWidth = 600,
            QuickWindowHeight = 320,
        };

        SettingsMigrations.Upgrade(settings);

        Assert.Equal(600, settings.QuickWindowWidth);
        Assert.Equal(320, settings.QuickWindowHeight);
    }

    [Fact]
    public void Upgrade_已是当前版本_不改动()
    {
        var settings = new AppSettings
        {
            SchemaVersion = SettingsMigrations.CurrentSchemaVersion,
            QuickWindowWidth = 420,
            QuickWindowHeight = 320,
        };

        SettingsMigrations.Upgrade(settings);

        Assert.Equal(420, settings.QuickWindowWidth);
        Assert.Equal(320, settings.QuickWindowHeight);
    }

    // ==================== 版本 2 → 3：单档 AI 迁移成供应商列表 ====================

    [Fact]
    public void Upgrade_旧版单档AI配置_搬成第一档供应商并清空遗留言段()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            LlmBaseUrl = "http://localhost:11434/v1",
            LlmModel = "qwen2.5",
            LlmApiKeyEncrypted = SecretStore.Protect("sk-old"),
        };

        SettingsMigrations.Upgrade(settings);

        var provider = Assert.Single(settings.LlmProviders);
        Assert.Equal(LlmProvider.DefaultId, provider.Id);
        Assert.Equal("本地模型", provider.Name);
        Assert.Equal("http://localhost:11434/v1", provider.BaseUrl);
        Assert.Equal("qwen2.5", provider.Model);
        Assert.Equal("sk-old", SecretStore.Unprotect(provider.ApiKeyEncrypted));
        Assert.Equal(LlmWireApi.Chat, provider.WireApi);
        Assert.True(provider.RequiresAuth);
        Assert.Equal(LlmProvider.DefaultId, settings.LlmActiveProviderId);
        // 此后供应商列表是唯一真相源
        Assert.Equal("", settings.LlmBaseUrl);
        Assert.Equal("", settings.LlmModel);
        Assert.Equal("", settings.LlmApiKeyEncrypted);
    }

    [Fact]
    public void Upgrade_旧版没填过AI_保留出厂默认值()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            LlmBaseUrl = "",
            LlmModel = "",
            LlmApiKeyEncrypted = "",
        };

        SettingsMigrations.Upgrade(settings);

        var provider = Assert.Single(settings.LlmProviders);
        Assert.Equal(LlmTranslator.DefaultBaseUrl, provider.BaseUrl);
        Assert.Equal(LlmTranslator.DefaultModel, provider.Model);
    }

    [Fact]
    public void Upgrade_云地址_档位名从域名推()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            LlmBaseUrl = "https://api.deepseek.com/v1",
        };

        SettingsMigrations.Upgrade(settings);

        // 出厂默认那一档沿用 LlmProvider.CreateDefault 的名字，不变成域名
        Assert.Equal("DeepSeek", settings.LlmProviders[0].Name);
    }

    [Fact]
    public void Upgrade_已是版本3_不重复迁移()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 3,
            LlmActiveProviderId = "custom",
            LlmProviders = [new LlmProvider { Id = "custom", Name = "公司网关", BaseUrl = "https://gw.internal/v1", Model = "gpt-4o" }],
        };

        SettingsMigrations.Upgrade(settings);

        Assert.Equal("公司网关", settings.LlmProviders[0].Name);
        Assert.Equal("custom", settings.LlmActiveProviderId);
    }
}
