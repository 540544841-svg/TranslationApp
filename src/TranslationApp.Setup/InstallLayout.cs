using System.IO;
using Microsoft.Win32;

namespace TranslationApp.Setup;

/// <summary>
/// 落址：装在哪、数据放哪、快捷方式写哪。三件事都是**当前用户**范围内的决定，
/// 因此全程不需要管理员——这也正是契书上「本机 · 当前用户」那一行的含义。
///
/// 数据落址与主程序 AppPaths 的规则一致（同一套判断，不另立一套）：
///   · 装在可移动盘 → 数据跟着程序走，落在 &lt;安装目录&gt;\data；
///   · 装在固定盘 → 数据落在 %AppData%\TranslationApp。
/// </summary>
internal static class InstallLayout
{
    public const string ProductName = "译印 INKSEAL";
    public const string AppFileName = "TranslationApp.exe";
    public const string UninstallKeyName = "译印 INKSEAL";

    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKeyName;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>默认落址：当前用户的 Programs 目录，双击即可装完，不弹 UAC。</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Inkseal");

    /// <summary>主程序落址。</summary>
    public static string AppPath(string installDirectory) =>
        Path.Combine(installDirectory, AppFileName);

    /// <summary>收印脚本落址（「应用和功能」里的卸载入口指向它）。</summary>
    public static string UnsealScriptPath(string installDirectory) =>
        Path.Combine(installDirectory, "unseal.ps1");

    /// <summary>数据落址。与主程序 AppPaths 同一规则。</summary>
    public static string DataDirectory(string installDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(installDirectory));
            if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Removable)
            {
                return Path.Combine(installDirectory, "data");
            }
        }
        catch
        {
            // 取不到驱动器信息（网络路径等）时按固定盘处理，与主程序一致：不猜。
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TranslationApp");
    }

    /// <summary>开始菜单里的那一枚印（当前用户）。</summary>
    public static string StartMenuLink =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            ProductName + ".lnk");

    /// <summary>桌面上的那一枚印（当前用户）。</summary>
    public static string DesktopLink =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ProductName + ".lnk");

    /// <summary>已安装？以「应用和功能」的登记为准——快捷方式可能被用户删掉，登记不会。</summary>
    public static bool TryGetInstalledDirectory(out string installDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        var location = key?.GetValue("InstallLocation") as string;
        if (!string.IsNullOrEmpty(location))
        {
            installDirectory = location;
            return true;
        }

        installDirectory = "";
        return false;
    }

    public static void WriteUninstallEntry(string installDirectory, string version, long payloadBytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入收印登记（注册表键被拒绝）。");

        var appPath = AppPath(installDirectory);
        var scriptPath = UnsealScriptPath(installDirectory);

        key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", "译印", RegistryValueKind.String);
        key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
        key.SetValue("DisplayIcon", appPath, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        // 收印走一个隐藏的 PowerShell 脚本：不弹控制台，装了什么就抹掉什么
        key.SetValue(
            "UninstallString",
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        // 估算大小（KB）：主程序单 EXE 是安装目录里唯一的实质文件
        key.SetValue("EstimatedSize", (int)Math.Max(1, payloadBytes / 1024), RegistryValueKind.DWord);
    }

    public static void RemoveUninstallEntry()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>开机自启：HKCU Run，值与主程序 AutoStart 的格式一致（"EXE" --minimized）。</summary>
    public static void SetAutoStart(string installDirectory, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入开机自启（注册表键被拒绝）。");

        if (enabled)
        {
            key.SetValue("TranslationApp", $"\"{AppPath(installDirectory)}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("TranslationApp", throwOnMissingValue: false);
        }
    }
}
