using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 注册表值读写抽象：让开机自启逻辑可用内存实现做确定性单测，
/// 避免直接操作真实 HKCU 带来的偶发失败（键被占用、安全软件拦截等）。
/// </summary>
internal interface IRegistryValueStore
{
    string? Get(string keyPath, string valueName);

    void Set(string keyPath, string valueName, string value);

    void Delete(string keyPath, string valueName);
}

/// <summary>真实实现：当前用户注册表。</summary>
[SupportedOSPlatform("windows")]
internal sealed class CurrentUserRegistryValueStore : IRegistryValueStore
{
    public static readonly CurrentUserRegistryValueStore Instance = new();

    public string? Get(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    public void Set(string keyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"无法写入注册表键：{keyPath}");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void Delete(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// 开机自启（FR-011）：写入/删除 HKCU\...\Run，值为 "EXE全路径" --minimized，
/// 无需管理员权限。每次开启时重写路径（自包含单文件移动位置后自动更新）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly IRegistryValueStore _registry;

    public AutoStart(string? valueName = null)
        : this(valueName, CurrentUserRegistryValueStore.Instance)
    {
    }

    internal AutoStart(string? valueName, IRegistryValueStore registry)
    {
        _valueName = valueName ?? "TranslationApp";
        _registry = registry;
    }

    /// <summary>是否已启用（仅检查注册表值存在）。</summary>
    public bool IsEnabled => RegisteredCommand is not null;

    /// <summary>当前注册的启动命令（未启用返回 null）。</summary>
    public string? RegisteredCommand => _registry.Get(RunKeyPath, _valueName);

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _registry.Delete(RunKeyPath, _valueName);
            return;
        }

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取当前 EXE 路径");
        _registry.Set(RunKeyPath, _valueName, FormatCommand(exePath));
    }

    /// <summary>
    /// 自愈：已启用、但注册的路径不是当前 EXE 时改写。自包含单文件被挪过位置后，
    /// 旧路径会让开机自启**静默失效**（Windows 直接忽略指向不存在文件的 Run 项），
    /// 用户只会觉得「怎么不自动启动了」——所以每次启动顺手校正一次。
    /// 未启用时什么都不做：关掉就是关掉，不自作主张帮用户打开。
    /// </summary>
    public void RepairIfEnabled()
    {
        var registered = RegisteredCommand;
        var exePath = Environment.ProcessPath;
        if (registered is null || exePath is null)
        {
            return;
        }

        var expected = FormatCommand(exePath);
        if (!string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase))
        {
            _registry.Set(RunKeyPath, _valueName, expected);
        }
    }

    private static string FormatCommand(string exePath) => $"\"{exePath}\" --minimized";
}
