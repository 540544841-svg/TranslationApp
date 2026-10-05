using System.Runtime.Versioning;
using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 开机自启逻辑测试（FR-011）：使用内存注册表实现，确定性验证命令格式与开关行为；
/// 真实注册表往返测试见文件末尾（默认跳过，需 TRANSLATIONAPP_LIVE_TESTS=1）。
/// </summary>
[SupportedOSPlatform("windows")]
public class AutoStartTests
{
    private sealed class InMemoryRegistry : IRegistryValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public int SetCount { get; private set; }

        public string? Get(string keyPath, string valueName) =>
            _values.TryGetValue($"{keyPath}\\{valueName}", out var value) ? value : null;

        public void Set(string keyPath, string valueName, string value)
        {
            SetCount++;
            _values[$"{keyPath}\\{valueName}"] = value;
        }

        public void Delete(string keyPath, string valueName) =>
            _values.Remove($"{keyPath}\\{valueName}");
    }

    [Fact]
    public void SetEnabled_True_RegistersQuotedExePathWithMinimizedFlag()
    {
        var registry = new InMemoryRegistry();
        var autoStart = new AutoStart("TranslationApp.Tests", registry);

        autoStart.SetEnabled(true);

        Assert.True(autoStart.IsEnabled);
        var command = autoStart.RegisteredCommand;
        Assert.NotNull(command);
        Assert.StartsWith("\"", command);                            // 路径带引号，防空格路径断裂
        Assert.Contains("--minimized", command);                     // 启动即最小化驻留托盘
        Assert.Contains(".exe", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetEnabled_TrueThenFalse_RemovesValue()
    {
        var autoStart = new AutoStart("TranslationApp.Tests", new InMemoryRegistry());

        autoStart.SetEnabled(true);
        Assert.True(autoStart.IsEnabled);

        autoStart.SetEnabled(false);
        Assert.False(autoStart.IsEnabled);
        Assert.Null(autoStart.RegisteredCommand);
    }

    [Fact]
    public void SetEnabled_False_WhenAlreadyAbsent_DoesNotThrow()
    {
        var autoStart = new AutoStart("TranslationApp.Tests", new InMemoryRegistry());

        autoStart.SetEnabled(false); // 未启用时关闭应静默成功

        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void SetEnabled_True_Twice_OverwritesPath()
    {
        var registry = new InMemoryRegistry();
        var autoStart = new AutoStart("TranslationApp.Tests", registry);

        autoStart.SetEnabled(true);
        autoStart.SetEnabled(true);

        // 每次开启都重写（EXE 移动位置后路径自动更新）
        Assert.Equal(2, registry.SetCount);
        Assert.True(autoStart.IsEnabled);
    }

    [Fact]
    public void IsEnabled_IndependentPerValueName()
    {
        var registry = new InMemoryRegistry();
        var app = new AutoStart("TranslationApp", registry);
        var other = new AutoStart("OtherApp.Tests", registry);

        app.SetEnabled(true);

        Assert.True(app.IsEnabled);
        Assert.False(other.IsEnabled);
    }

    [Fact]
    public void RepairIfEnabled_WhenDisabled_DoesNotRegister()
    {
        var registry = new InMemoryRegistry();
        var autoStart = new AutoStart("TranslationApp.Tests", registry);

        autoStart.RepairIfEnabled();

        // 关掉就是关掉：自愈不能自作主张帮用户打开
        Assert.Equal(0, registry.SetCount);
        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void RepairIfEnabled_WhenCommandMatches_DoesNotWrite()
    {
        var registry = new InMemoryRegistry();
        var autoStart = new AutoStart("TranslationApp.Tests", registry);
        autoStart.SetEnabled(true);
        var before = registry.SetCount;

        autoStart.RepairIfEnabled();

        // 已经指向自己就不该再写注册表（每次启动都写是没必要的系统副作用）
        Assert.Equal(before, registry.SetCount);
    }

    [Fact]
    public void RepairIfEnabled_WhenPathIsStale_RewritesToCurrentExe()
    {
        var registry = new InMemoryRegistry();
        var autoStart = new AutoStart("TranslationApp.Tests", registry);
        autoStart.SetEnabled(true);
        registry.Set(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            "TranslationApp.Tests",
            "\"C:\\Old\\Inkseal.exe\" --minimized");
        var before = registry.SetCount;

        autoStart.RepairIfEnabled();

        Assert.Equal(before + 1, registry.SetCount);
        var command = autoStart.RegisteredCommand;
        Assert.NotNull(command);
        Assert.Contains(Environment.ProcessPath!, command);
        Assert.Contains("--minimized", command);
    }
}

/// <summary>真实注册表往返测试：默认跳过（需 TRANSLATIONAPP_LIVE_TESTS=1），测后清理。</summary>
[SupportedOSPlatform("windows")]
public class AutoStartLiveTests
{
    [Fact]
    public void SetEnabled_RealRegistry_RoundTrip()
    {
        if (Environment.GetEnvironmentVariable("TRANSLATIONAPP_LIVE_TESTS") != "1")
        {
            return;
        }

        var autoStart = new AutoStart("TranslationApp.Tests.Live");
        try
        {
            autoStart.SetEnabled(true);
            Assert.True(autoStart.IsEnabled);
            Assert.Contains("--minimized", autoStart.RegisteredCommand!);

            autoStart.SetEnabled(false);
            Assert.False(autoStart.IsEnabled);
        }
        finally
        {
            autoStart.SetEnabled(false);
        }
    }
}
