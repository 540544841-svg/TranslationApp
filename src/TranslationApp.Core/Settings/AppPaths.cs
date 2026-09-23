namespace TranslationApp.Core.Settings;

/// <summary>
/// 本地数据路径。默认 %AppData%\TranslationApp；EXE 同目录存在 portable.flag 时，
/// 设置、历史、词典、日志与更新缓存全部切到 EXE 同目录 data 下，便于整目录迁移。
/// </summary>
public static class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static bool IsPortable =>
        string.Equals(Environment.GetEnvironmentVariable("TRANSLATIONAPP_PORTABLE"), "1", StringComparison.Ordinal)
        || File.Exists(Path.Combine(BaseDirectory, PortableFlagName));

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

    /// <summary>切换便携模式标记；安装目录不可写时抛由调用方提示。</summary>
    public static void SetPortable(bool enabled)
    {
        var flag = Path.Combine(BaseDirectory, PortableFlagName);
        if (enabled)
        {
            File.WriteAllText(flag, "1");
        }
        else if (File.Exists(flag))
        {
            File.Delete(flag);
        }
    }
}
