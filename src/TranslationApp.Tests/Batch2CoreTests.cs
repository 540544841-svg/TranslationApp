using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>P0 批 2 / spec §4：GlossaryCache 全局启用门控（关闭 = 空表直通，零替换）。</summary>
public class GlossaryCacheTests
{
    private const string TwoItems = """[{"Source":"apple","Target":"苹果","Enabled":true}]""";

    [Fact]
    public void Enabled_ReturnsParsedItems()
    {
        var settings = new AppSettings { GlossaryJson = TwoItems, GlossaryEnabled = true };
        var cache = new GlossaryCache(() => settings.GlossaryJson, () => settings.GlossaryEnabled);

        Assert.Single(cache.Items());
    }

    [Fact]
    public void Disabled_ReturnsEmpty_AndRecoversOnReEnable()
    {
        var settings = new AppSettings { GlossaryJson = TwoItems, GlossaryEnabled = false };
        var cache = new GlossaryCache(() => settings.GlossaryJson, () => settings.GlossaryEnabled);

        Assert.Empty(cache.Items());

        settings.GlossaryEnabled = true;
        Assert.Single(cache.Items()); // JSON 未变也要按最新开关给结果

        settings.GlossaryEnabled = false;
        Assert.Empty(cache.Items());
    }

    [Fact]
    public void LegacyCtor_WithoutEnabledProvider_BehavesAsAlwaysEnabled()
    {
        var cache = new GlossaryCache(() => TwoItems);
        Assert.Single(cache.Items());
    }
}

/// <summary>P0 批 2 新字段：默认值与旧配置文件向后兼容（spec §1.2/§2.3/§3.1）。</summary>
public class Batch2SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "TranslationAppTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Defaults_Batch2Fields()
    {
        var settings = new AppSettings();
        Assert.False(settings.AnkiEnabled);
        Assert.True(settings.AnkiPushOnFavorite);
        Assert.Equal("生词本", settings.AnkiDeck);
        Assert.Equal("基本", settings.AnkiModel);
        Assert.Equal("正面", settings.AnkiFrontField);
        Assert.Equal("背面", settings.AnkiBackField);
        Assert.False(settings.HoverSelectEnabled);
        Assert.True(settings.GlossaryEnabled);
        Assert.Equal("", settings.ActiveProfile);
        Assert.Equal("[]", settings.CustomProfilesJson);
        Assert.Equal("Alt+P", settings.HotkeySwitchProfile);
    }

    [Fact]
    public void LegacyJsonWithoutBatch2Fields_LoadsWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"Engine":"deepl","PrivacyMode":true}""");

        var settings = new JsonSettingsStore(FilePath).Load();
        Assert.Equal("deepl", settings.Engine);
        Assert.True(settings.PrivacyMode);
        Assert.True(settings.GlossaryEnabled);
        Assert.Equal("", settings.ActiveProfile);
    }

    [Fact]
    public void Batch2Fields_RoundTrip()
    {
        var store = new JsonSettingsStore(FilePath);
        var settings = new AppSettings
        {
            AnkiEnabled = true,
            AnkiDeck = "Ozon 俄语",
            HoverSelectEnabled = true,
            GlossaryEnabled = false,
            ActiveProfile = "阅读",
            CustomProfilesJson = """[{"Name":"我的档","Overrides":{"Engine":"azure"}}]""",
        };
        store.Save(settings);

        var loaded = store.Load();
        Assert.True(loaded.AnkiEnabled);
        Assert.Equal("Ozon 俄语", loaded.AnkiDeck);
        Assert.True(loaded.HoverSelectEnabled);
        Assert.False(loaded.GlossaryEnabled);
        Assert.Equal("阅读", loaded.ActiveProfile);
        Assert.Equal(settings.CustomProfilesJson, loaded.CustomProfilesJson);
    }
}
