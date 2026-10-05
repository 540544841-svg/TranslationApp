using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

    /// <summary>按时间：落在纸时段用纸，其余用墨（分界可配置，见 ThemePaperFromHour/ToHour）。</summary>
    Auto,
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

    /// <summary>
    /// 生效深浅色真正发生变化时触发（手动切换、跟随系统变化、按时间档到点自动切换都算）。
    /// 供界面刷新「此刻按时间 → 纸/墨」这类随主题变化的提示文案。
    /// </summary>
    public static event Action? EffectiveChanged;

    /// <summary>按时间档的默认分界：早六点转纸、晚六点转墨。</summary>
    public const int DefaultPaperFromHour = 6;
    public const int DefaultPaperToHour = 18;

    /// <summary>
    /// 「按时间」档的纸时段起点 / 终点小时（来自设置，用户可在设置页改）。
    /// Apply 是无状态的静态方法，时段只能放在静态属性上，由启动流程与设置页写入。
    /// </summary>
    public static int PaperFromHour { get; private set; } = DefaultPaperFromHour;
    public static int PaperToHour { get; private set; } = DefaultPaperToHour;

    /// <summary>从设置写入纸时段（越界/颠倒的值会被规整，见 NormalizePaperHours）。</summary>
    public static void ConfigurePaperHours(int from, int to)
    {
        (PaperFromHour, PaperToHour) = NormalizePaperHours(from, to);
    }

    /// <summary>
    /// 把设置里的时段规整成一对可用的 (起, 止)：越界或起点不早于终点一律退回默认，
    /// 免得手改配置文件后整天反常。仅支持同日区间（不支持跨夜）。
    /// </summary>
    private static (int From, int To) NormalizePaperHours(int from, int to) =>
        from is >= 0 and <= 23 && to is >= 1 and <= 23 && from < to
            ? (from, to)
            : (DefaultPaperFromHour, DefaultPaperToHour);

    /// <summary>
    /// 按设置里的小时算出「此刻是否纸」以及「下次翻转的小时」，供提示文案用。
    /// 纯函数，不读静态状态，因此设置页改完时段立刻能算出新文案。
    /// </summary>
    public static (bool IsPaper, int FlipHour) ResolvePaperWindow(int from, int to, DateTime now)
    {
        var (f, t) = NormalizePaperHours(from, to);
        var isPaper = now.Hour >= f && now.Hour < t;
        return (isPaper, isPaper ? t : f);
    }

    /// <summary>该时刻是否落在「纸」的那一段。</summary>
    public static bool IsPaperHour(DateTime now) =>
        now.Hour >= PaperFromHour && now.Hour < PaperToHour;

    /// <summary>按设置应用主题，并刷新所有已打开窗口的标题栏。</summary>
    public static void Apply(AppTheme theme)
    {
        // 记住旧值：按时间档每分钟重算，只在真正跨过切换点时才通知界面刷新提示文案
        var wasDark = IsDarkEffective;
        IsDarkEffective = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            AppTheme.Auto => !IsPaperHour(DateTime.Now),
            _ => IsSystemDark(),
        };
        SyncClock(theme);

        if (wasDark != IsDarkEffective)
        {
            EffectiveChanged?.Invoke();
        }

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

    /// <summary>
    /// 按时间档要自己到点换肤：挂一个每分钟醒一次的计时器，跨过六点/十八点就重算。
    /// 只在该档下运行，换回手动或跟随系统时立刻停掉。
    /// </summary>
    private static void SyncClock(AppTheme theme)
    {
        if (theme != AppTheme.Auto)
        {
            _clock?.Stop();
            return;
        }

        // DispatcherTimer 构造即开跑；重入时只是重新 Start，无副作用
        _clock ??= new DispatcherTimer(
            TimeSpan.FromMinutes(1), DispatcherPriority.Background,
            (_, _) => Apply(AppTheme.Auto), Dispatcher.CurrentDispatcher);
        _clock.Start();
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

    /// <summary>按时间档的到点换肤计时器，只在 AppTheme.Auto 下运行。</summary>
    private static DispatcherTimer? _clock;

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
