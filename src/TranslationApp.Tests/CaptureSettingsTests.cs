using System.Runtime.Versioning;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// FR-021 设置字段与热键（13.7）：默认值、持久化、Alt+O 解析。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CaptureSettingsTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"ta-capture-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_MatchDocumentation()
    {
        var settings = new AppSettings();

        Assert.Equal("Alt+O", settings.HotkeyCaptureTranslate);
        Assert.Equal("auto", settings.OcrLanguage);
        Assert.True(settings.OcrAutoTranslate);
        Assert.Equal(0.55, settings.OcrScrimOpacity, 10);
    }

    [Fact]
    public void LegacySettingsFile_WithoutCaptureFields_UsesDefaults()
    {
        // 旧配置文件必须能读，且取到 13.7 规定的默认值
        File.WriteAllText(_settingsPath, """{"Engine":"bing","TargetLanguage":"zh-CN"}""");

        var settings = new JsonSettingsStore(_settingsPath).Load();

        Assert.Equal("Alt+O", settings.HotkeyCaptureTranslate);
        Assert.Equal("auto", settings.OcrLanguage);
        Assert.True(settings.OcrAutoTranslate);
        Assert.Equal(0.55, settings.OcrScrimOpacity, 10);
    }

    [Fact]
    public void CaptureSettings_RoundTrip()
    {
        var store = new JsonSettingsStore(_settingsPath);
        var settings = store.Load();

        settings.HotkeyCaptureTranslate = "Ctrl+Shift+O";
        settings.OcrLanguage = "zh-Hans-CN";
        settings.OcrAutoTranslate = false;
        settings.OcrScrimOpacity = 0.75;
        store.Save(settings);

        var fileContent = File.ReadAllText(_settingsPath);
        Assert.Contains("HotkeyCaptureTranslate", fileContent);
        Assert.Contains("OcrLanguage", fileContent);
        Assert.Contains("OcrAutoTranslate", fileContent);
        Assert.Contains("OcrScrimOpacity", fileContent);

        var reloaded = store.Load();
        Assert.Equal("Ctrl+Shift+O", reloaded.HotkeyCaptureTranslate);
        Assert.Equal("zh-Hans-CN", reloaded.OcrLanguage);
        Assert.False(reloaded.OcrAutoTranslate);
        Assert.Equal(0.75, reloaded.OcrScrimOpacity, 10);
    }

    [Fact]
    public void CaptureHotkey_AltO_ParsesToModifierAndVirtualKey()
    {
        Assert.True(HotkeyDefinition.TryParse("Alt+O", out var definition));
        Assert.Equal(HotkeyModifiers.Alt, definition.Modifiers);
        Assert.Equal('O', definition.VirtualKey);
        Assert.Equal(HotkeyDefinition.DefaultCapture, definition);
        Assert.Equal("Alt+O", definition.ToString());
        Assert.True(definition.IsValidKey);
    }

    [Fact]
    public void CaptureHotkey_IsDistinctFromExistingDefaults()
    {
        // 三个默认热键不得互相冲突
        Assert.NotEqual(HotkeyDefinition.DefaultCapture, HotkeyDefinition.DefaultInput);
        Assert.NotEqual(HotkeyDefinition.DefaultCapture, HotkeyDefinition.DefaultSelect);
    }

    [Fact]
    public void CaptureHotkey_CorruptedValue_FallsBackToDefault()
    {
        Assert.Equal(
            HotkeyDefinition.DefaultCapture,
            HotkeyDefinition.ParseOrDefault("这不是热键", HotkeyDefinition.DefaultCapture));
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
