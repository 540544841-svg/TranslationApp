using System.Windows;

namespace TranslationApp.Setup;

/// <summary>
/// 安装器主题：与主程序同一条品牌规则——浅色=纸、深色=墨，且「纸时段」与
/// 主程序设置里的 ThemePaperFromHour / ThemePaperToHour 同源（默认早六点到晚六点）。
///
/// 安装器只做一件事：按**安装发生的时刻**选一套色板（设计稿：配色随安装时刻）。
/// 不做「跟随系统」、不做分钟级到点翻转——窗口只活几分钟，翻色只会让人以为闪屏。
/// </summary>
internal static class SetupTheme
{
    private const string PaperSource = "Themes/Tokens.Theme.Paper.xaml";
    private const string InkSource = "Themes/Tokens.Theme.Ink.xaml";

    /// <summary>纸时段起点 / 终点（小时）。与主程序默认值一致。</summary>
    public const int PaperFromHour = 6;
    public const int PaperToHour = 18;

    /// <summary>当前生效的是否为墨（深色）。</summary>
    public static bool IsDark { get; private set; }

    /// <summary>该时刻是否落在「纸」的那一段。</summary>
    public static bool IsPaperHour(DateTime now) => now.Hour >= PaperFromHour && now.Hour < PaperToHour;

    /// <summary>按安装时刻定色，返回选中的是哪一套（供界面文案说明）。</summary>
    public static bool ApplyByInstallTime(DateTime now) => Apply(IsPaperHour(now));

    /// <summary>整本替换第 0 号字典（主题色板）即完成换肤，与主程序 ThemeManager 同一手法。</summary>
    public static bool Apply(bool paper)
    {
        IsDark = !paper;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return paper;
        }

        var source = paper ? PaperSource : InkSource;
        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var dictionaries = resources.MergedDictionaries;
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = replacement;
        }
        else
        {
            dictionaries.Add(replacement);
        }

        return paper;
    }
}
