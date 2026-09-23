using TranslationApp.Core.Diagnostics;
using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

public sealed class UserExperienceFeatureTests
{
    [Fact]
    public void ReplaceSelection_DefaultsToDirectPaste()
    {
        var settings = new AppSettings();

        Assert.True(settings.ReplaceWritesHistory);
        Assert.False(settings.ReplaceSelectionEnabled);
        Assert.Equal("Alt+R", settings.HotkeyReplaceTranslate);
    }

    [Theory]
    [InlineData("hotkey:输入翻译", "navigate:hotkeys", "打开热键设置")]
    [InlineData("ocr-windows", "navigate:advanced", "检查 OCR 设置")]
    [InlineData("proxy", "navigate:advanced", "检查代理设置")]
    [InlineData("database", "open-data-folder", "打开数据目录")]
    [InlineData("update-channel", "navigate:updates", "打开更新与数据")]
    public void DoctorAction_MapsActionableResultToStableAction(
        string id,
        string expectedKey,
        string expectedLabel)
    {
        var result = new DiagnosticResult(
            id,
            "诊断项",
            DiagnosticStatus.Failed,
            "需要处理",
            "给出修复建议");

        var action = DiagnosticActionCatalog.Resolve(result);

        Assert.NotNull(action);
        Assert.Equal(expectedKey, action.Key);
        Assert.Equal(expectedLabel, action.Label);
    }

    [Fact]
    public void DoctorAction_ActualHotkeyResultId_OpensHotkeySettings()
    {
        // DoctorService 实际生成的是 hotkey-input，而不是示例里的 hotkey:输入翻译。
        var result = new DiagnosticResult(
            "hotkey-input",
            "全局热键 · 输入翻译",
            DiagnosticStatus.Failed,
            "未注册");

        var action = DiagnosticActionCatalog.Resolve(result);

        Assert.NotNull(action);
        Assert.Equal("navigate:hotkeys", action.Key);
        Assert.Equal("打开热键设置", action.Label);
    }

    [Fact]
    public void DoctorAction_PassedResultDoesNotDemandAction()
    {
        var result = new DiagnosticResult(
            "proxy",
            "诊断项",
            DiagnosticStatus.Passed,
            "正常");

        var action = DiagnosticActionCatalog.Resolve(result);

        Assert.Null(action);
    }

    [Fact]
    public void DoctorAction_UnknownWarningStillOffersRerun()
    {
        var result = new DiagnosticResult(
            "future-check",
            "诊断项",
            DiagnosticStatus.Warning,
            "需要复测");

        var action = DiagnosticActionCatalog.Resolve(result);

        Assert.NotNull(action);
        Assert.Equal("rerun", action.Key);
        Assert.Equal("重新诊断", action.Label);
    }
}
