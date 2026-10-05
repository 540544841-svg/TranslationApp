namespace TranslationApp.Core.Settings;

/// <summary>
/// 本地数据路径。默认 %AppData%\TranslationApp；下列任一成立时改写到 EXE 同目录 data 下：
/// 1) 环境变量 TRANSLATIONAPP_PORTABLE=1（绿色版分发用）；
/// 2) EXE 同目录存在 portable.flag，且内容不是 off；
/// 3) 自动推断——EXE 在可移动驱动器上且该目录可写（U 盘随身场景，不拿选择题烦用户）。
/// 用户显式关掉便携时写入内容为 off 的 portable.flag，压住自动推断，让用户的选择算数。
/// </summary>
public static class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    /// <summary>portable.flag 的这个内容表示「用户明确要求留在系统目录」。</summary>
    private const string PortableOff = "off";

    private static readonly Lazy<bool> WritableBase = new(ProbeBaseDirectory);
    private static readonly Lazy<bool> Portable = new(ResolvePortable);

    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>EXE 所在目录是否可写；Program Files 这类受保护目录为 false。</summary>
    public static bool CanWriteBaseDirectory => WritableBase.Value;

    /// <summary>EXE 是否位于可移动驱动器（U 盘、移动硬盘）。</summary>
    public static bool IsOnRemovableDrive
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(BaseDirectory));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Removable;
            }
            catch
            {
                // 取不到驱动器信息（网络路径、权限异常）时按固定盘处理，不猜。
                return false;
            }
        }
    }

    /// <summary>未显式设置时的自动推断：可移动盘且可写，就走便携。</summary>
    public static bool AutoPortable => CanWriteBaseDirectory && IsOnRemovableDrive;

    /// <summary>进程内固定：切换便携模式后需重启才生效（与设置页的提示一致）。</summary>
    public static bool IsPortable => Portable.Value;

    /// <summary>
    /// 磁盘上的标记与本次运行已生效的落址是否不同。不同就说明用户刚改过开关、要重启才切过去，
    /// 设置页据此决定挂不挂「重启生效」角标——没改过就不挂，别把常驻提示当装饰。
    /// </summary>
    public static bool PortableRestartPending => ResolvePortable() != IsPortable;

    public static string DataDirectory => IsPortable
        ? Path.Combine(BaseDirectory, "data")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TranslationApp");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryDatabaseFile => Path.Combine(DataDirectory, "history.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DictionariesDirectory => Path.Combine(DataDirectory, "dicts");

    public static string UpdatesDirectory => Path.Combine(DataDirectory, "updates");

    public static string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>
    /// 切换便携模式标记。安装目录不可写时抛 <see cref="InvalidOperationException"/>，
    /// 消息已按用户可读写好，设置页直接展示即可。
    /// </summary>
    public static void SetPortable(bool enabled)
    {
        var flag = Path.Combine(BaseDirectory, PortableFlagName);
        if (!enabled)
        {
            // 写 off 而不是删文件：在可移动盘上删掉标记会被自动推断重新打开，用户的选择就白做了。
            File.WriteAllText(flag, PortableOff);
            return;
        }

        if (!CanWriteBaseDirectory)
        {
            throw new InvalidOperationException(
                $"EXE 所在目录不可写（{BaseDirectory}），便携模式无法开启；请把译印移到用户目录或 U 盘再试。");
        }

        File.WriteAllText(flag, "on");
    }

    private static bool ResolvePortable()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("TRANSLATIONAPP_PORTABLE"),
                "1",
                StringComparison.Ordinal))
        {
            return true;
        }

        var flag = Path.Combine(BaseDirectory, PortableFlagName);
        if (File.Exists(flag))
        {
            try
            {
                return !string.Equals(File.ReadAllText(flag).Trim(), PortableOff, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 读不出内容时按「已开启」处理：文件存在本身就是旧版语义。
                return true;
            }
        }

        return AutoPortable;
    }

    private static bool ProbeBaseDirectory()
    {
        try
        {
            var probe = Path.Combine(BaseDirectory, ".inkseal-write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
