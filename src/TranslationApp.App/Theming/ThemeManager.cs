using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Serilog;

namespace TranslationApp.Theming;

/// <summary>界面主题（FR-019）。</summary>
public enum AppTheme
{
    /// <summary>跟随系统（默认）。</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// 主题管理：整本替换主题色板资源字典即可让全部界面即时换肤
/// （所有控件颜色均以 DynamicResource 引用语义令牌）。
/// 同时处理 Windows 原生标题栏的深色适配（DWM 沉浸式深色模式），
/// 否则深色主题下会出现突兀的白色标题栏。
/// </summary>
public static class ThemeManager
{
    // 译印 INKSEAL：浅色=纸，深色=墨
    private const string PaperSource = "Themes/Tokens.Theme.Paper.xaml";
    private const string InkSource = "Themes/Tokens.Theme.Ink.xaml";

    /// <summary>DWM 属性：沉浸式深色模式（Windows 10 1809+ 用 20，旧版本用 19）。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>当前生效的是否为深色。</summary>
    public static bool IsDarkEffective { get; private set; }

    /// <summary>按设置应用主题，并刷新所有已打开窗口的标题栏。</summary>
    public static void Apply(AppTheme theme)
    {
        IsDarkEffective = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark(),
        };

        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var source = IsDarkEffective ? InkSource : PaperSource;
        var dictionaries = resources.MergedDictionaries;

        // 主题字典固定位于第 0 位（见 App.xaml 合并顺序），整本替换即可换肤
        var existing = dictionaries.Count > 0 ? dictionaries[0] : null;
        if (existing is not null
            && existing.Source is not null
            && existing.Source.OriginalString.EndsWith(
                IsDarkEffective ? "Tokens.Theme.Ink.xaml" : "Tokens.Theme.Paper.xaml", StringComparison.Ordinal))
        {
            ApplyTitleBarToOpenWindows();
            return; // 已是目标主题
        }

        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = replacement;
        }
        else
        {
            dictionaries.Add(replacement);
        }

        Log.Information("已切换主题：{Theme}（深色={IsDark}）", theme, IsDarkEffective);
        ApplyTitleBarToOpenWindows();
    }

    /// <summary>给某个窗口应用/更新原生标题栏深浅色（窗口创建时调用）。</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = IsDarkEffective ? 1 : 0;
        try
        {
            // 先试新属性，失败再试旧属性（不同 Windows 版本属性号不同）
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // 极旧系统无 dwmapi，忽略（仅影响标题栏配色）
        }
    }

    private static void ApplyTitleBarToOpenWindows()
    {
        if (Application.Current is null)
        {
            return;
        }

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }
    }

    /// <summary>读取系统「应用模式」是否为深色（注册表），失败按浅色处理。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取系统主题失败，按浅色处理");
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
