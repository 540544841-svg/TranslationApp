using TranslationApp.Core.Settings;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-037：场景档案（稀疏覆盖 / 循环 / 快照 / 偏离判定）。spec §3。</summary>
public class ProfileServiceTests
{
    private static ProfileService NewService(AppSettings settings) => new(settings);

    [Fact]
    public void Reading_AppliesSixKeys_KeepsUnspecifiedKeys()
    {
        var settings = new AppSettings
        {
            Engine = "deepl",
            ClipboardMonitorEnabled = true,
            HoverSelectEnabled = true,
        };
        NewService(settings).Apply("阅读");

        Assert.Equal("bing", settings.Engine);
        Assert.Equal("auto", settings.SourceLanguage);
        Assert.Equal("zh-CN", settings.TargetLanguage);
        Assert.True(settings.CleanClipboardText);
        Assert.False(settings.PrivacyMode);
        Assert.True(settings.GlossaryEnabled);
        Assert.Equal("阅读", settings.ActiveProfile);
        // 未指定的键不动（行为开关不代用户决定，spec §3.2）
        Assert.True(settings.ClipboardMonitorEnabled);
        Assert.True(settings.HoverSelectEnabled);
    }

    [Fact]
    public void Privacy_TurnsOffTracesAndHooks()
    {
        var settings = new AppSettings
        {
            ClipboardMonitorEnabled = true,
            HoverSelectEnabled = true,
        };
        NewService(settings).Apply("隐私");

        Assert.True(settings.PrivacyMode);
        Assert.False(settings.ClipboardMonitorEnabled);
        Assert.False(settings.HoverSelectEnabled);
        Assert.True(settings.GlossaryEnabled);
        Assert.Equal("隐私", settings.ActiveProfile);
    }

    [Fact]
    public void Standard_OnlyClearsActiveProfile_KeysUntouched()
    {
        var settings = new AppSettings { Engine = "deepl" };
        NewService(settings).Apply("阅读");
        settings.Engine = "baidu";

        NewService(settings).Apply("标准");

        Assert.Equal("", settings.ActiveProfile);
        Assert.Equal("baidu", settings.Engine);
    }

    [Fact]
    public void UnknownProfileName_IsNoOp_ExceptActiveProfile()
    {
        var settings = new AppSettings { Engine = "deepl" };
        NewService(settings).Apply("不存在的档案");
        Assert.Equal("deepl", settings.Engine);
        Assert.Equal("", settings.ActiveProfile);
    }

    [Fact]
    public void NextProfileName_Cycles_StandardReadingPrivacyWritingCustom()
    {
        var settings = new AppSettings();
        var service = NewService(settings);

        Assert.Equal("阅读", service.NextProfileName()); // 标准 → 阅读
        settings.ActiveProfile = "阅读";
        Assert.Equal("隐私", service.NextProfileName());
        settings.ActiveProfile = "隐私";
        Assert.Equal("写作", service.NextProfileName()); // FR-054 内置三档
        settings.ActiveProfile = "写作";
        Assert.Equal("标准", service.NextProfileName()); // 无自定义时回到标准

        Assert.True(service.SaveCurrentAs("我的档"));
        settings.ActiveProfile = "写作";
        Assert.Equal("我的档", service.NextProfileName());
        settings.ActiveProfile = "我的档";
        Assert.Equal("标准", service.NextProfileName());
    }

    [Fact]
    public void SaveCurrentAs_SnapshotsCurrentValues()
    {
        var settings = new AppSettings
        {
            Engine = "deepl",
            TargetLanguage = "ru",
            PrivacyMode = true,
        };
        var service = NewService(settings);

        Assert.True(service.SaveCurrentAs("俄语档"));
        settings.ActiveProfile = "写作";
        Assert.Equal("俄语档", service.NextProfileName()); // 自定义档接在最后

        // 把当前值改成别的，再应用自定义档应还原快照
        settings.Engine = "bing";
        settings.TargetLanguage = "zh-CN";
        settings.PrivacyMode = false;
        service.Apply("俄语档");
        Assert.Equal("deepl", settings.Engine);
        Assert.Equal("ru", settings.TargetLanguage);
        Assert.True(settings.PrivacyMode);
    }

    [Fact]
    public void SaveCurrentAs_RejectsEmptyDuplicateTooLong()
    {
        var settings = new AppSettings();
        var service = NewService(settings);

        Assert.False(service.SaveCurrentAs("  "));
        Assert.True(service.SaveCurrentAs("我的档"));
        Assert.False(service.SaveCurrentAs("我的档"));
        Assert.False(service.SaveCurrentAs(new string('词', 21)));
        Assert.Single(service.CustomProfiles());
    }

    [Fact]
    public void SaveCurrentAs_RejectsWhenAtCapacity()
    {
        var settings = new AppSettings();
        var service = NewService(settings);
        for (var i = 1; i <= ProfileService.MaxCustomProfiles; i++)
        {
            Assert.True(service.SaveCurrentAs($"档{i}"));
        }
        Assert.False(service.SaveCurrentAs("档11"));
    }

    [Fact]
    public void DeleteCustom_RemovesAndKeepsOthers()
    {
        var settings = new AppSettings();
        var service = NewService(settings);
        service.SaveCurrentAs("甲");
        service.SaveCurrentAs("乙");

        Assert.True(service.DeleteCustom("甲"));
        Assert.False(service.DeleteCustom("甲")); // 再删不存在 → false
        var restored = Assert.Single(service.CustomProfiles());
        Assert.Equal("乙", restored.Name);
    }

    [Fact]
    public void IsDeviation_TrueWhenAnyOverriddenKeyDiffers()
    {
        var settings = new AppSettings();
        var service = NewService(settings);

        service.Apply("阅读");
        Assert.False(service.IsDeviation());

        settings.Engine = "deepl";
        Assert.True(service.IsDeviation());

        settings.Engine = "bing";
        Assert.False(service.IsDeviation());

        service.Apply("标准");
        settings.Engine = "deepl"; // 标准档无覆盖键，改什么都不算偏离
        Assert.False(service.IsDeviation());
    }

    [Fact]
    public void CustomProfilesJson_RoundTripsThroughSettings()
    {
        var settings = new AppSettings { Engine = "azure" };
        var service = NewService(settings);
        service.SaveCurrentAs("我的档");

        // 新实例（模拟重启后从落盘 JSON 恢复）
        var service2 = NewService(settings);
        var restored = Assert.Single(service2.CustomProfiles());
        Assert.Equal("我的档", restored.Name);
        settings.Engine = "bing";
        service2.Apply("我的档");
        Assert.Equal("azure", settings.Engine);
    }

    [Fact]
    public void CorruptedCustomProfilesJson_TreatedAsEmpty_AndRepairable()
    {
        var settings = new AppSettings { CustomProfilesJson = "{这不是 JSON" };
        var service = NewService(settings);

        Assert.Empty(service.CustomProfiles());
        Assert.Equal(3, service.AllProfiles().Count); // 仅内置三档
        Assert.True(service.SaveCurrentAs("重建档")); // 保存时覆盖坏数据
        var restored = Assert.Single(service.CustomProfiles());
        Assert.Equal("重建档", restored.Name);
    }

    [Fact]
    public void AllProfiles_BuiltInFirst_ThenCustom()
    {
        var settings = new AppSettings();
        var service = NewService(settings);
        service.SaveCurrentAs("我的档");
        Assert.Equal(["阅读", "隐私", "写作", "我的档"], service.AllProfiles().Select(p => p.Name));
    }
}
