using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>设置存储读写冒烟测试（阶段 0 验收：dotnet test 全绿）。</summary>
public class SettingsStoreTests : IDisposable
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
    public void SaveThenLoad_RoundTrip_PreservesValues()
    {
        var store = new JsonSettingsStore(FilePath);
        var settings = new AppSettings
        {
            TargetLanguage = "ja",
            Engine = "baidu",
            HotkeyInputTranslate = "Ctrl+Q",
            ShowStartBalloon = false,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.TargetLanguage, loaded.TargetLanguage);
        Assert.Equal(settings.Engine, loaded.Engine);
        Assert.Equal(settings.HotkeyInputTranslate, loaded.HotkeyInputTranslate);
        Assert.Equal(settings.ShowStartBalloon, loaded.ShowStartBalloon);
        Assert.Equal(settings.SourceLanguage, loaded.SourceLanguage); // 未修改字段保持默认
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var store = new JsonSettingsStore(FilePath);

        var loaded = store.Load();

        Assert.Equal("Alt+D", loaded.HotkeyInputTranslate);
        Assert.Equal("Alt+S", loaded.HotkeySelectTranslate);
        // 默认引擎需为零配置且国内网络可直连的 Bing（Google 接口在部分网络不可达）
        Assert.Equal("bing", loaded.Engine);
        Assert.Equal("zh-CN", loaded.TargetLanguage);
        Assert.True(loaded.ShowStartBalloon);
    }

    [Fact]
    public void Load_WhenFileCorrupt_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not valid json !!!");

        var store = new JsonSettingsStore(FilePath);
        var loaded = store.Load();

        Assert.Equal(SettingsMigrations.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("zh-CN", loaded.TargetLanguage);
    }

    [Fact]
    public void Save_CreatesDirectory_OverwritesAndCleansTemp()
    {
        var store = new JsonSettingsStore(FilePath);

        store.Save(new AppSettings { TargetLanguage = "en" });
        store.Save(new AppSettings { TargetLanguage = "ko" });
        var loaded = store.Load();

        Assert.Equal("ko", loaded.TargetLanguage);
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"), "临时文件应在替换后清理");
    }
}
