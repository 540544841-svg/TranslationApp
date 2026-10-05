using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using TranslationApp.Core.Anki;
using TranslationApp.Core.Backup;
using TranslationApp.Core.History;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Layout;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Core.Updates;
using TranslationApp.Services;
using TranslationApp.Theming;
// 别名：本类会生成同名属性 OcrOutputMode，直接用类型名会被属性遮蔽（FR-027）
using OcrMode = TranslationApp.Core.Capture.OcrOutputMode;

namespace TranslationApp.ViewModels;

/// <summary>主题下拉项。</summary>
public sealed record ThemeOption(string Value, string Display);

/// <summary>「按时间」主题里的小时下拉项（如 06:00）。</summary>
public sealed record HourOption(int Hour, string Display);

/// <summary>代理作用范围下拉项（FR-018）。</summary>
public sealed record ProxyModeOption(string Value, string Display);

/// <summary>
/// 历史页的一行：会话组标题，或一条记录。
/// 分组视图原先是「外层 ItemsControl 套内层 ItemsControl」：内层拿不到有限高度，
/// UI 虚拟化失效，500 条记录要一次性实例化 500 份行模板，这是切到历史页卡顿的主因。
/// 摊平成一层后由单个 ListBox 渲染，每行只在进入视口时才创建视觉树。
/// </summary>
public abstract record HistoryRow;

public sealed record HistoryHeaderRow(int GroupIndex, string Label, string ToggleText) : HistoryRow;

public sealed record HistoryRecordRow(TranslationRecord Record) : HistoryRow;

/// <summary>
/// 设置窗口 ViewModel（FR-009/014/015/016/017/018/019）：配置即时保存（变更即写盘，无确定按钮）。
/// 热键变更立即重注册生效（FR-001），注册失败恢复原热键并标红提示。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly HotkeyManager _hotkeyManager;
    private readonly AutoStart _autoStart;
    private readonly IHistoryRepository _history;
    private readonly IVocabularyRepository _vocabulary;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly ITtsService _tts;
    private readonly TranslatorCatalog _catalog;
    private readonly HttpClientProvider _httpProvider;
    private readonly OcrService _ocr;
    private readonly TranslationApp.Core.History.EngineStatsRepository _engineStats;
    private readonly MouseButtonHook _mouseHook;
    private readonly KeyboardButtonHook _keyboardHook;
    private readonly ProfileService _profiles;
    private readonly TranslationApp.Services.LocalApiGateway _localApiGateway;
    private readonly AnkiConnectClient _anki;
    private readonly BackupService _backup;
    private readonly UpdateService _updates;
    private readonly TranslationApp.Services.DoctorService _doctor;
    private readonly TranslationApp.Services.InPlaceTranslationService _inPlaceTranslations;
    private bool _suppressAutoStartCallback;
    private bool _suppressPasswordCallback;
    private long _historyRefreshVersion;
    private readonly HashSet<int> _collapsedGroups = [];

    public QuickTranslateViewModel Workbench { get; }

    public SettingsViewModel(
        AppSettings settings,
        ISettingsStore store,
        HotkeyManager hotkeyManager,
        AutoStart autoStart,
        TranslatorCatalog catalog,
        HttpClientProvider httpProvider,
        IHistoryRepository history,
        IVocabularyRepository vocabulary,
        ClipboardMonitor clipboardMonitor,
        ITtsService tts,
        OcrService ocr,
        TranslationApp.Core.History.EngineStatsRepository engineStats,
        MouseButtonHook mouseHook,
        KeyboardButtonHook keyboardHook,
        ProfileService profiles,
        TranslationApp.Services.LocalApiGateway localApiGateway,
        AnkiConnectClient anki,
        BackupService backup,
        UpdateService updates,
        TranslationApp.Services.DoctorService doctor,
        TranslationApp.Services.InPlaceTranslationService inPlaceTranslations,
        TranslationApp.Core.Dictionary.DictionaryManager? dictionaries = null,
        QuickTranslateViewModel? workbench = null)
    {
        Workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
        _settings = settings;
        _store = store;
        _hotkeyManager = hotkeyManager;
        _autoStart = autoStart;
        _catalog = catalog;
        _httpProvider = httpProvider;
        _history = history;
        _vocabulary = vocabulary;
        _clipboardMonitor = clipboardMonitor;
        _tts = tts;
        _ocr = ocr;
        _engineStats = engineStats;
        _mouseHook = mouseHook;
        _keyboardHook = keyboardHook;
        _profiles = profiles;
        _localApiGateway = localApiGateway;
        _anki = anki;
        _backup = backup;
        _updates = updates;
        _doctor = doctor;
        _inPlaceTranslations = inPlaceTranslations;
        _dictionaries = dictionaries;

        _showStartBalloon = settings.ShowStartBalloon;
        _launchRevealEnabled = settings.LaunchRevealEnabled;
        _feedbackSoundEnabled = settings.FeedbackSoundEnabled;
        _autoStartEnabled = autoStart.IsEnabled;
        _targetLanguage = settings.TargetLanguage;
        _hotkeyInputText = settings.HotkeyInputTranslate;
        _hotkeySelectText = settings.HotkeySelectTranslate;
        _hotkeyCaptureText = settings.HotkeyCaptureTranslate;
        _hotkeyProfileText = settings.HotkeySwitchProfile;
        _hotkeyReplaceText = settings.HotkeyReplaceTranslate;
        _updateManifestUrl = settings.UpdateManifestUrl;
        _updateAutoCheck = settings.UpdateAutoCheck;
        _portableMode = AppPaths.IsPortable;
        _replaceSelectionEnabled = settings.ReplaceSelectionEnabled;
        _replaceWritesHistory = settings.ReplaceWritesHistory;
        _selectedTheme = NormalizeTheme(settings.Theme);
        _themePaperFromHour = settings.ThemePaperFromHour;
        _themePaperToHour = settings.ThemePaperToHour;
        ThemeManager.EffectiveChanged += OnEffectiveThemeChanged;

        // 「引擎」页（FR-024）先建卡片，再决定当前引擎是否可用（未配置则回退并提示）
        InitializeEnginePage();
        _selectedEngine = catalog.Resolve(settings.Engine).Id;

        _clipboardMonitorEnabled = settings.ClipboardMonitorEnabled;
        _autoSpeakAfterSelect = settings.AutoSpeakAfterSelect;
        // P0 批 1：隐私模式 / 自动整理换行 / 术语表
        _privacyMode = settings.PrivacyMode;
        _cleanClipboardText = settings.CleanClipboardText;
        InitializeGlossaryPage();
        // P0 批 2：悬停取词 / 术语全局开关 / Anki 直推 / 场景模式
        _hoverSelectEnabled = settings.HoverSelectEnabled;
        _glossaryEnabled = settings.GlossaryEnabled;
        InitializeAnkiPage();
        InitializeProfilePage();
        // P0 批 3：双击修饰键 / 鼠标侧键 / 粘贴即译
        _doubleTapTranslateEnabled = settings.DoubleTapTranslateEnabled;
        _doubleTapKey = NormalizeDoubleTapKey(settings.DoubleTapKey);
        _mouseSideButtonSelect = settings.MouseSideButtonSelect;
        _mouseSideButtonCapture = settings.MouseSideButtonCapture;
        _pasteTranslateEnabled = settings.PasteTranslateEnabled;
        _tmReuseEnabled = settings.TmReuseEnabled;
        _llmContextEnabled = settings.LlmContextEnabled;
        _dailyReviewEnabled = settings.DailyReviewEnabled;
        _shadowReadingEnabled = settings.ShadowReadingEnabled;
        _shadowPauseMs = settings.ShadowPauseMs;
        _historyGroupedView = settings.HistoryGroupedView;
        _appLanguageMemoryEnabled = settings.AppLanguageMemoryEnabled;
        RefreshAppLanguageRules();
        // P0 批 4：本地 HTTP API（状态行需反映监听实况，构造时刷一次）
        InitializeApiPage();
        // P0 批 4：本地 mdx 词典（列表要显示「装了但解析不了」的项，构造时扫一次）
        InitializeDictionariesPage();
        // 诊断页：先铺出全部检查项（「待检查」），App 启动时那次自动诊断随后把每行改成结论
        InitializeDoctorPage();

        // FR-026「通用 → 小窗尺寸」：默认宽高即设置里的 QuickWindowWidth/Height（见 AppSettings 注释），
        // 开关沿用 QuickWindowSizeMode（auto = 按内容自适应 / manual = 固定用默认宽高）
        _quickWindowAutoSize = !WindowSizePolicy.IsManual(settings.QuickWindowSizeMode);
        _quickWindowDefaultWidth = WindowSizePolicy.ClampWidth(settings.QuickWindowWidth);
        _quickWindowDefaultHeight = WindowSizePolicy.ClampHeight(settings.QuickWindowHeight);

        // 「高级 → 截图翻译（OCR）」分区（13.5.1）：先建下拉项，再回填设置值
        BuildOcrLanguageOptions();
        _ocrLanguage = NormalizeOcrLanguage(settings.OcrLanguage);
        _ocrAutoTranslate = settings.OcrAutoTranslate;
        _ocrScrimOpacity = Math.Clamp(settings.OcrScrimOpacity, MinScrimOpacity, MaxScrimOpacity);
        _ocrOutputMode = OcrMode.Parse(settings.OcrOutputMode); // FR-027：未知值按默认 pin
        // FR-029-1（14.3.12.4）：识别前预处理三态，未知值按默认 auto（旧配置缺字段向后兼容）
        _ocrPreprocess = TranslationApp.Core.Capture.OcrPreprocess.NormalizeMode(settings.OcrPreprocess);
        // FR-030（14.9.3）：本地识别引擎两态 + paddle 会话常驻开关，未知值按默认 windows
        _ocrLocalEngine = TranslationApp.Core.Capture.OcrEngineNames.Normalize(settings.OcrLocalEngine);
        _ocrPaddleResident = settings.OcrPaddleResident;
        // FR-027（14.6）：钉图相关三项，归属同一卡片（14.7 不新开页面）
        _ocrInPlaceReplace = settings.OcrInPlaceReplace;
        _pinZoomStep = TranslationApp.Core.Capture.PinLayout.ClampZoomStep(settings.PinZoomStep);
        _pinToolbarAutoFade = settings.PinToolbarAutoFade;

        _proxyEnabled = settings.ProxyEnabled;
        _selectedProxyMode = NormalizeProxyMode(settings.ProxyMode);
        _proxyHost = settings.ProxyHost;
        _proxyScheme = NormalizeProxyScheme(settings.ProxyScheme);
        _proxyPort = settings.ProxyPort;
        _proxyUserName = settings.ProxyUserName;
        // 密码框显示已保存的密码（明文仅在内存中，落盘走 DPAPI 密文）
        _suppressPasswordCallback = true;
        _proxyPassword = SecretStore.Unprotect(settings.ProxyPasswordEncrypted) ?? "";
        _suppressPasswordCallback = false;

        _ttsAvailable = tts.InstalledVoiceCount > 0;

        if (_clipboardMonitorEnabled)
        {
            _clipboardMonitor.Start();
        }
    }

    // ==================== 通用 ====================

    public IReadOnlyList<TranslationLanguages.LanguageOption> TargetOptions => TranslationLanguages.TargetOptions;

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        // 放在首位：它是默认档，当前选中项在左边一眼能看见
        new("auto", "按时间"),
        new("system", "跟随系统"),
        // 品牌语：浅色叫「纸」、深色叫「墨」，一脉相承；具体含义交给行说明交代
        new("light", "纸"),
        new("dark", "墨"),
    ];

    // 「纸的时段」两个下拉各给一段候选（起点 0~22、终点 1~23），
    // 让「起 < 止」天然成立；两端仍会互相顶推，见 OnThemePaper*HourChanged。
    public IReadOnlyList<HourOption> PaperFromHourOptions { get; } =
        [.. Enumerable.Range(0, 23).Select(h => new HourOption(h, $"{h:00}:00"))];

    public IReadOnlyList<HourOption> PaperToHourOptions { get; } =
        [.. Enumerable.Range(1, 23).Select(h => new HourOption(h, $"{h:00}:00"))];

    /// <summary>「纸的时段」只有按时间档才现身（其他档位下这两个小时没有意义）。</summary>
    public bool IsThemeAuto => SelectedTheme == "auto";

    /// <summary>
    /// 按时间档的一句话现状：「此刻 02:18 属墨 · 06:00 转纸」。
    /// 用户最困惑的就是「为什么现在是黑的」，这里直接把他此刻所处的位置和下一个切换点报出来。
    /// </summary>
    public string ThemeAutoStatus
    {
        get
        {
            var (isPaper, flipHour) = ThemeManager.ResolvePaperWindow(
                ThemePaperFromHour, ThemePaperToHour, DateTime.Now);
            return $"此刻 {DateTime.Now:HH:mm} 属{(isPaper ? "纸" : "墨")} · {flipHour:00}:00 转{(isPaper ? "墨" : "纸")}";
        }
    }

    public IReadOnlyList<ProxyModeOption> ProxyModeOptions { get; } =
    [
        new("googleOnly", "仅国外引擎（Google / Azure / DeepL / AI）"),
        new("all", "全部引擎"),
    ];

    [ObservableProperty]
    private bool _showStartBalloon;

    /// <summary>启动时播放「启印」落印动画（手动启动；开机自启不播）。</summary>
    [ObservableProperty]
    private bool _launchRevealEnabled;

    /// <summary>操作确认音效（默认关）：截图识别等关键动作成功后短提示。</summary>
    [ObservableProperty]
    private bool _feedbackSoundEnabled;

    partial void OnFeedbackSoundEnabledChanged(bool value) => Save(s => s.FeedbackSoundEnabled = value);

    /// <summary>默认只展示日常功能，首次启动不再被全部设置项淹没。</summary>
    [ObservableProperty]
    private bool _showAdvancedSettings;

    public string AdvancedSettingsToggleText => ShowAdvancedSettings ? "收起高级设置" : "显示高级设置";

    partial void OnShowAdvancedSettingsChanged(bool value) =>
        OnPropertyChanged(nameof(AdvancedSettingsToggleText));

    [RelayCommand]
    private void ToggleAdvancedSettings() => ShowAdvancedSettings = !ShowAdvancedSettings;

    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private string _targetLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeAuto))]
    [NotifyPropertyChangedFor(nameof(ThemeAutoStatus))]
    private string _selectedTheme;

    /// <summary>按时间档的纸时段起点小时（默认 6，设置页可改）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeAutoStatus))]
    private int _themePaperFromHour;

    /// <summary>按时间档的纸时段终点小时（默认 18，设置页可改）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeAutoStatus))]
    private int _themePaperToHour;

    [ObservableProperty]
    private string _selectedEngine;

    [ObservableProperty]
    private string _hotkeyInputText;

    [ObservableProperty]
    private string _hotkeySelectText;

    /// <summary>FR-021：截图翻译热键（默认 Alt+O）。</summary>
    [ObservableProperty]
    private string _hotkeyCaptureText;

    /// <summary>FR-037（批 2）：场景模式循环切换热键（默认 Alt+P）。</summary>
    [ObservableProperty]
    private string _hotkeyProfileText;

    /// <summary>翻译并原位替换热键（默认 Alt+R，功能开关另控）。</summary>
    [ObservableProperty]
    private string _hotkeyReplaceText;

    [ObservableProperty]
    private string _hotkeyInputStatus = "";

    [ObservableProperty]
    private string _hotkeySelectStatus = "";

    [ObservableProperty]
    private string _hotkeyCaptureStatus = "";

    [ObservableProperty]
    private string _hotkeyProfileStatus = "";

    [ObservableProperty]
    private string _hotkeyReplaceStatus = "";

    [ObservableProperty]
    private IReadOnlyList<string> _hotkeyInputSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _hotkeySelectSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _hotkeyCaptureSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _hotkeyProfileSuggestions = [];

    [ObservableProperty]
    private IReadOnlyList<string> _hotkeyReplaceSuggestions = [];

    [ObservableProperty]
    private bool _replaceSelectionEnabled;

    /// <summary>原位替换成功后是否写入翻译历史；隐私模式下仍由主链路强制不写。</summary>
    [ObservableProperty]
    private bool _replaceWritesHistory = true;

    [ObservableProperty]
    private bool _hotkeyInputInvalid;

    [ObservableProperty]
    private bool _hotkeySelectInvalid;

    [ObservableProperty]
    private bool _hotkeyCaptureInvalid;
    [ObservableProperty]
    private bool _hotkeyReplaceInvalid;

    [ObservableProperty]
    private bool _hotkeyProfileInvalid;

    [ObservableProperty]
    private string _hotkeyMessage = "";

    [ObservableProperty]
    private string _generalMessage = "";

    // ==================== FR-026 小窗尺寸 ====================

    /// <summary>
    /// 「通用 → 小窗尺寸 → 按内容自适应」。关闭 = 每次呼出都严格使用下面的默认宽高。
    /// </summary>
    [ObservableProperty]
    private bool _quickWindowAutoSize = true;

    private double _quickWindowDefaultWidth = WindowSizePolicy.DefaultWidthDip;
    private double _quickWindowDefaultHeight = WindowSizePolicy.DefaultHeightDip;

    /// <summary>
    /// 默认宽度（DIP，含阴影留白的窗口尺寸）。赋值即夹取到 320~900 并落盘，
    /// 因此设置卡片里的输入框直接双向绑定本属性即可。
    /// </summary>
    public double QuickWindowDefaultWidth
    {
        get => _quickWindowDefaultWidth;
        set
        {
            var clamped = WindowSizePolicy.ClampWidth(value);
            var changed = Math.Abs(clamped - _quickWindowDefaultWidth) > 0.001;
            if (changed)
            {
                _quickWindowDefaultWidth = clamped;
            }

            // 总是通知：越界输入（如 2000）夹取后可能与当前值相同，但仍需让输入框回显正确值
            OnPropertyChanged();
            if (changed)
            {
                Save(s => s.QuickWindowWidth = clamped);
            }
        }
    }

    /// <summary>默认高度（DIP）。赋值即夹取到 240~900 并落盘。</summary>
    public double QuickWindowDefaultHeight
    {
        get => _quickWindowDefaultHeight;
        set
        {
            var clamped = WindowSizePolicy.ClampHeight(value);
            var changed = Math.Abs(clamped - _quickWindowDefaultHeight) > 0.001;
            if (changed)
            {
                _quickWindowDefaultHeight = clamped;
            }

            OnPropertyChanged();
            if (changed)
            {
                Save(s => s.QuickWindowHeight = clamped);
            }
        }
    }

    partial void OnQuickWindowAutoSizeChanged(bool value)
    {
        Save(s => s.QuickWindowSizeMode = value ? WindowSizePolicy.AutoMode : WindowSizePolicy.ManualMode);
        Log.Information("小窗尺寸模式已切换为 {Mode}", value ? "按内容自适应" : "固定使用默认宽高");
    }

    /// <summary>「恢复推荐默认值」：宽高重置为设计稿小窗尺寸（开关状态不变）。</summary>
    [RelayCommand]
    private void RestoreRecommendedDefaultSize()
    {
        QuickWindowDefaultWidth = WindowSizePolicy.DefaultWidthDip;
        QuickWindowDefaultHeight = WindowSizePolicy.DefaultHeightDip;
    }

    partial void OnShowStartBalloonChanged(bool value) => Save(s => s.ShowStartBalloon = value);

    partial void OnLaunchRevealEnabledChanged(bool value) => Save(s => s.LaunchRevealEnabled = value);

    partial void OnSelectedThemeChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Save(s => s.Theme = value);
        ThemeManager.Apply(ToAppTheme(value));
    }

    /// <summary>改了纸时段起点：顶推终点保证「起 < 止」，存盘并按新时段重算一次。</summary>
    partial void OnThemePaperFromHourChanged(int value)
    {
        if (value >= ThemePaperToHour)
        {
            ThemePaperToHour = value + 1;
        }

        Save(s => s.ThemePaperFromHour = value);
        ReapplyAutoTheme();
    }

    /// <summary>改了纸时段终点：顶推起点保证「起 < 止」，存盘并按新时段重算一次。</summary>
    partial void OnThemePaperToHourChanged(int value)
    {
        if (value <= ThemePaperFromHour)
        {
            ThemePaperFromHour = value - 1;
        }

        Save(s => s.ThemePaperToHour = value);
        ReapplyAutoTheme();
    }

    /// <summary>
    /// 时段或档位变了就立刻重算一次：改完要马上看到纸/墨翻过来，而不是等下一分钟计时器醒来。
    /// </summary>
    private void ReapplyAutoTheme()
    {
        ThemeManager.ConfigurePaperHours(ThemePaperFromHour, ThemePaperToHour);
        if (SelectedTheme == "auto")
        {
            ThemeManager.Apply(AppTheme.Auto);
        }
    }

    /// <summary>到点自动换肤时（ThemeManager 的分钟计时器）刷新行内「此刻」提示。</summary>
    private void OnEffectiveThemeChanged() => OnPropertyChanged(nameof(ThemeAutoStatus));

    partial void OnSelectedEngineChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && _settings.Engine != value)
        {
            Save(s => s.Engine = value);
            GeneralMessage = "";
            EngineMessage = ""; // 用户已主动切换，回退提示不再适用
            RefreshCompareEngineTags(); // 未勾选对比引擎时，摘要取的是「当前引擎 + 首个已配置引擎」
            RefreshCurrentEngineFlags(); // 引擎页的「当前引擎」朱砂标签随之换位
        }
    }

    partial void OnTargetLanguageChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Save(s => s.TargetLanguage = value);
        }
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_suppressAutoStartCallback)
        {
            return;
        }

        try
        {
            _autoStart.SetEnabled(value);
            GeneralMessage = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "开机自启设置失败");
            _suppressAutoStartCallback = true;
            AutoStartEnabled = _autoStart.IsEnabled;
            _suppressAutoStartCallback = false;
            GeneralMessage = "开机自启设置失败";
        }
    }

    // ==================== 热键 ====================

    /// <summary>四个可录制热键的槽位（FR-001/005/021；Profile 为 FR-037）。</summary>
    private enum HotkeySlot
    {
        Input,
        Select,
        Capture,
        Profile,
        Replace,
    }

    partial void OnHotkeyInputTextChanged(string value) => ApplyHotkey(HotkeySlot.Input, value);

    partial void OnHotkeySelectTextChanged(string value) => ApplyHotkey(HotkeySlot.Select, value);

    partial void OnHotkeyCaptureTextChanged(string value) => ApplyHotkey(HotkeySlot.Capture, value);

    partial void OnHotkeyProfileTextChanged(string value) => ApplyHotkey(HotkeySlot.Profile, value);

    partial void OnHotkeyReplaceTextChanged(string value) => ApplyHotkey(HotkeySlot.Replace, value);

    partial void OnReplaceSelectionEnabledChanged(bool value)
    {
        Save(s => s.ReplaceSelectionEnabled = value);
        if (value)
        {
            ApplyHotkey(HotkeySlot.Replace, HotkeyReplaceText, force: true);
        }
        else
        {
            _hotkeyManager.Unregister("replace");
            HotkeyReplaceInvalid = false;
        }
    }

    partial void OnReplaceWritesHistoryChanged(bool value) => Save(s => s.ReplaceWritesHistory = value);


    [RelayCommand]
    private void ResetHotkeys()
    {
        ApplyHotkey(HotkeySlot.Input, HotkeyDefinition.DefaultInput.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Select, HotkeyDefinition.DefaultSelect.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Capture, HotkeyDefinition.DefaultCapture.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Profile, HotkeyDefinition.DefaultProfile.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Replace, HotkeyDefinition.DefaultReplace.ToString(), force: true);
        HotkeyInputText = HotkeyDefinition.DefaultInput.ToString();
        HotkeySelectText = HotkeyDefinition.DefaultSelect.ToString();
        HotkeyCaptureText = HotkeyDefinition.DefaultCapture.ToString();
        HotkeyProfileText = HotkeyDefinition.DefaultProfile.ToString();
        HotkeyReplaceText = HotkeyDefinition.DefaultReplace.ToString();
    }

    [RelayCommand]
    private void ResetHotkey(string? slot)
    {
        var parsed = Enum.TryParse<HotkeySlot>(slot, ignoreCase: true, out var value)
            ? value
            : HotkeySlot.Input;
        var defaultValue = DefaultOf(parsed).ToString();
        ApplyHotkey(parsed, defaultValue, force: true);
        SetHotkeyText(parsed, defaultValue);
        SetStatus(parsed, $"{defaultValue} 已恢复默认");
    }

    private void ApplyHotkey(HotkeySlot slot, string value, bool force = false)
    {
        var current = CurrentHotkeyText(slot);
        if (!force && string.Equals(current, value, StringComparison.Ordinal) && _hotkeyManager.IsRegistered(NameOf(slot)))
        {
            if (!IsInvalid(slot))
            {
                SetStatus(slot, $"{value} 可用");
            }
            return;
        }

        if (!HotkeyDefinition.TryParse(value, out var newDefinition))
        {
            SetInvalid(slot, true);
            SetStatus(slot, "格式不支持：需要 Ctrl / Alt / Shift / Win + 字母、数字或 F1~F12");
            HotkeyMessage = "热键格式不正确，请按提示重新录入";
            return;
        }

        // 「翻译并替换」未启用时不占用系统热键，只保存配置供下次启用。
        if (slot == HotkeySlot.Replace && !ReplaceSelectionEnabled)
        {
            Save(s => s.HotkeyReplaceTranslate = value);
            SetInvalid(slot, false);
            SetStatus(slot, $"{value} 已保存；启用替换后注册");
            SetSuggestions(slot, []);
            return;
        }

        // 各热键之间不得重复
        var oldDefinition = HotkeyDefinition.ParseOrDefault(current, DefaultOf(slot));
        foreach (var other in OtherSlots(slot))
        {
            if (HotkeyDefinition.TryParse(CurrentHotkeyText(other), out var otherDefinition)
                && otherDefinition == newDefinition)
            {
                SetInvalid(slot, true);
                SetStatus(slot, $"{newDefinition} 与“{LabelOf(other)}”重复；已保留原热键 {oldDefinition}");
                HotkeyMessage = "功能之间的热键不能重复";
                SetHotkeyText(slot, oldDefinition.ToString());
                SetSuggestions(slot, BuildSuggestions(slot, newDefinition));
                return;
            }
        }


        if (_hotkeyManager.TryRegister(NameOf(slot), newDefinition))
        {
            Save(s =>
            {
                switch (slot)
                {
                    case HotkeySlot.Input:
                        s.HotkeyInputTranslate = value;
                        break;
                    case HotkeySlot.Select:
                        s.HotkeySelectTranslate = value;
                        break;
                    case HotkeySlot.Profile:
                        s.HotkeySwitchProfile = value;
                        break;
                    case HotkeySlot.Replace:
                        s.HotkeyReplaceTranslate = value;
                        break;
                    default:
                        s.HotkeyCaptureTranslate = value;
                        break;
                }
            });
            SetInvalid(slot, false);
            foreach (var other in OtherSlots(slot))
            {
                SetInvalid(other, false);
            }
            SetStatus(slot, $"{newDefinition} 可用");
            foreach (var other in OtherSlots(slot))
            {
                SetStatus(other, "");
            }
            SetSuggestions(slot, []);

            HotkeyMessage = "";
            return;
        }

        _hotkeyManager.TryRegister(NameOf(slot), oldDefinition);
        SetInvalid(slot, true);
        SetStatus(slot, $"{newDefinition} 已被系统或其他程序占用；原热键 {oldDefinition} 仍可用");
        HotkeyMessage = $"热键 {newDefinition} 注册失败，请换一个组合";
        SetHotkeyText(slot, oldDefinition.ToString());
        SetSuggestions(slot, BuildSuggestions(slot, newDefinition));
    }

    private static string NameOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => "input",
        HotkeySlot.Select => "select",
        HotkeySlot.Profile => "profile",
        HotkeySlot.Replace => "replace",
        _ => "capture",
    };

    private static HotkeyDefinition DefaultOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => HotkeyDefinition.DefaultInput,
        HotkeySlot.Select => HotkeyDefinition.DefaultSelect,
        HotkeySlot.Profile => HotkeyDefinition.DefaultProfile,
        HotkeySlot.Replace => HotkeyDefinition.DefaultReplace,
        _ => HotkeyDefinition.DefaultCapture,
    };

    private static string LabelOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => "输入翻译",
        HotkeySlot.Select => "划词翻译",
        HotkeySlot.Capture => "截图翻译",
        HotkeySlot.Profile => "场景模式切换",
        _ => "翻译并替换",
    };

    private static IEnumerable<HotkeySlot> OtherSlots(HotkeySlot slot) =>
        Enum.GetValues<HotkeySlot>().Where(other => other != slot);

    private IReadOnlyList<string> BuildSuggestions(HotkeySlot slot, HotkeyDefinition requested)
    {
        var appOwned = OtherSlots(slot)
            .Select(other => HotkeyDefinition.TryParse(CurrentHotkeyText(other), out var definition)
                ? definition
                : (HotkeyDefinition?)null)
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();

        return HotkeySuggestionGenerator.Suggest(
                requested,
                appOwned,
                candidate => _hotkeyManager.CanRegister(NameOf(slot), candidate))
            .Select(definition => definition.ToString())
            .ToArray();
    }

    private string CurrentHotkeyText(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => _settings.HotkeyInputTranslate,
        HotkeySlot.Select => _settings.HotkeySelectTranslate,
        HotkeySlot.Profile => _settings.HotkeySwitchProfile,
        HotkeySlot.Replace => _settings.HotkeyReplaceTranslate,
        _ => _settings.HotkeyCaptureTranslate,
    };

    private void SetHotkeyText(HotkeySlot slot, string value)
    {
        switch (slot)
        {
            case HotkeySlot.Input:
                HotkeyInputText = value;
                break;
            case HotkeySlot.Select:
                HotkeySelectText = value;
                break;
            case HotkeySlot.Profile:
                HotkeyProfileText = value;
                break;
            case HotkeySlot.Replace:
                HotkeyReplaceText = value;
                break;
            default:
                HotkeyCaptureText = value;
                break;
        }
    }

    private void SetInvalid(HotkeySlot slot, bool invalid)
    {
        switch (slot)
        {
            case HotkeySlot.Input:
                HotkeyInputInvalid = invalid;
                break;
            case HotkeySlot.Select:
                HotkeySelectInvalid = invalid;
                break;
            case HotkeySlot.Profile:
                HotkeyProfileInvalid = invalid;
                break;
            case HotkeySlot.Replace:
                HotkeyReplaceInvalid = invalid;
                break;
            default:
                HotkeyCaptureInvalid = invalid;
                break;
        }
    }

    private bool IsInvalid(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => HotkeyInputInvalid,
        HotkeySlot.Select => HotkeySelectInvalid,
        HotkeySlot.Profile => HotkeyProfileInvalid,
        HotkeySlot.Replace => HotkeyReplaceInvalid,
        _ => HotkeyCaptureInvalid,
    };

    private void SetStatus(HotkeySlot slot, string status)
    {
        switch (slot)
        {
            case HotkeySlot.Input:
                HotkeyInputStatus = status;
                break;
            case HotkeySlot.Select:
                HotkeySelectStatus = status;
                break;
            case HotkeySlot.Profile:
                HotkeyProfileStatus = status;
                break;
            case HotkeySlot.Replace:
                HotkeyReplaceStatus = status;
                break;
            default:
                HotkeyCaptureStatus = status;
                break;
        }
    }

    private void SetSuggestions(HotkeySlot slot, IReadOnlyList<string> suggestions)
    {
        switch (slot)
        {
            case HotkeySlot.Input:
                HotkeyInputSuggestions = suggestions;
                break;
            case HotkeySlot.Select:
                HotkeySelectSuggestions = suggestions;
                break;
            case HotkeySlot.Profile:
                HotkeyProfileSuggestions = suggestions;
                break;
            case HotkeySlot.Replace:
                HotkeyReplaceSuggestions = suggestions;
                break;
            default:
                HotkeyCaptureSuggestions = suggestions;
                break;
        }
    }

    // ==================== FR-014 历史记录 ====================

    /// <summary>历史页当前渲染的行序列（平铺时全是记录行；分组时标题行交错记录行）。</summary>
    [ObservableProperty]
    private ObservableCollection<HistoryRow> _historyRows = [];

    // ==================== 工作台摘要（设计稿 .wb-strip / .wb-side）====================

    /// <summary>「最近落印」条数：与印谱页的筛选互不影响，工作台永远看最新的几条。</summary>
    private const int WorkbenchRecentLimit = 4;

    /// <summary>
    /// 工作台右栏的「最近落印」。「以前从 HistoryRows 里取前三条」，那会把印谱页的选择
    /// （关键字 / 只看固定 / 引擎筛选）带到工作台上，因此改成独立查询。
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<HistoryRecordRow> _workbenchRecentRows = [];

    /// <summary>
    /// 工作台「今日印记」条的今日落印数。这是条上唯一跨重启保持真实的数字——
    /// 历史库只存文本，不存耗时与命中次数，所以条上其余三项在 QuickTranslateViewModel
    /// 里按本次运行累计（见其 SessionXxx 注释）。
    /// </summary>
    [ObservableProperty]
    private int _todaySealCount;

    /// <summary>当前场景（档案）显示名，供工作台「场景 · 阅读」标签使用。</summary>
    public string ProfileDisplayName => _profiles.DisplayName;

    /// <summary>工作台条上的场景标签。</summary>
    public string WorkbenchSceneTag => $"场景 · {ProfileDisplayName}";

    /// <summary>工作台条上的隐私标签。隐私开着时历史是不写的，这件事得写在脸上。</summary>
    public string WorkbenchPrivacyTag => PrivacyMode ? "隐私模式 已开" : "隐私模式 已关";

    /// <summary>
    /// 切到工作台时刷新整页摘要：今日落印数、最近落印、引擎行状态、场景名。
    /// 查询走后台线程——工作台是首页，不能因为几条统计把窗口拖住。
    /// </summary>
    public async Task RefreshWorkbenchAsync()
    {
        OnPropertyChanged(nameof(ProfileDisplayName));
        OnPropertyChanged(nameof(WorkbenchSceneTag));
        OnPropertyChanged(nameof(WorkbenchPrivacyTag));
        RefreshWorkbenchEngineRows();
        try
        {
            var today = HistoryTimeRanges.StartOf(HistoryTimeRanges.Today);
            var (count, recent) = await Task.Run(() =>
            {
                var todayCount = _history.Search(new HistoryQuery { From = today }).Count;
                var latest = _history.Search(new HistoryQuery { Limit = WorkbenchRecentLimit })
                    .Select(record => new HistoryRecordRow(record)).ToArray();
                return (todayCount, (IReadOnlyList<HistoryRecordRow>)latest);
            });
            TodaySealCount = count;
            WorkbenchRecentRows = recent;
        }
        catch (Exception ex)
        {
            // 首页不该因为一条统计查询失败就报错：拿不到就保持上一次的值
            Log.Warning(ex, "刷新工作台摘要失败");
        }
    }


    /// <summary>列表里的记录条数（ListBox 没有 HasItems，空状态用它判断）。</summary>
    [ObservableProperty]
    private int _historyCount;

    /// <summary>历史页是否走分组视图（关掉或有搜索词时回到平铺）。</summary>
    [ObservableProperty]
    private bool _historyGroupedView;

    partial void OnHistoryGroupedViewChanged(bool value)
    {
        Save(s => s.HistoryGroupedView = value);
        _ = RefreshHistoryAsync();
    }

    /// <summary>
    /// 实际生效的分组开关：搜索时强制平铺——跨组命中的结果按相关性排，
    /// 再按会话切组会让"最该看的那条"藏在某个组里。
    /// </summary>
    public bool IsHistoryGrouped => HistoryGroupedView && string.IsNullOrWhiteSpace(HistoryKeyword);

    [ObservableProperty]
    private string _historyKeyword = "";

    [ObservableProperty]
    private string _historyMessage = "";

    [ObservableProperty]
    private bool _historyPinnedOnly;

    [ObservableProperty]
    private string _historyEngineFilter = "";

    [ObservableProperty]
    private string _historyTargetLanguageFilter = "";

    [ObservableProperty]
    private string _historyTimeFilter = HistoryTimeRanges.All;

    partial void OnHistoryPinnedOnlyChanged(bool value) => _ = RefreshHistoryAsync();

    partial void OnHistoryEngineFilterChanged(string value) => _ = RefreshHistoryAsync();

    partial void OnHistoryTargetLanguageFilterChanged(string value) => _ = RefreshHistoryAsync();

    partial void OnHistoryTimeFilterChanged(string value) => _ = RefreshHistoryAsync();

    // ==================== 检索行的胶囩文案（设计稿 .tag.line） ====================
    // 下拉项直接写人话（「全部引擎 / 近 30 天」），空字符串只存在于查询层；
    // 显示值与查询值分开存，中文不会流进 HistoryQuery。
    public const string AllEnginesLabel = "全部引擎";

    public const string AllLanguagesLabel = "全部语言";

    [ObservableProperty]
    private string _historyEngineFilterLabel = AllEnginesLabel;

    [ObservableProperty]
    private string _historyTargetLanguageFilterLabel = AllLanguagesLabel;

    [ObservableProperty]
    private string _historyTimeFilterLabel = "全部时间";

    partial void OnHistoryEngineFilterLabelChanged(string value) =>
        HistoryEngineFilter = EngineRawFor(value);

    partial void OnHistoryTargetLanguageFilterLabelChanged(string value) =>
        HistoryTargetLanguageFilter = value == AllLanguagesLabel ? "" : value;

    /// <summary>胶囩文案 → 库里的引擎原名（Engine 列存的是目录全名，筛选靠它精确匹配）。</summary>
    private readonly Dictionary<string, string> _historyEngineRawByLabel = new(StringComparer.Ordinal);

    private string EngineRawFor(string label)
    {
        if (label.Length == 0 || label == AllEnginesLabel)
        {
            return "";
        }

        return _historyEngineRawByLabel.TryGetValue(label, out var raw) ? raw : label;
    }

    /// <summary>原名 → 胶囩文案；两枚引擎撞了短名就退回全名，保证一枚胶囩只对一个引擎。</summary>
    private string EngineLabelFor(string raw)
    {
        if (raw.Length == 0)
        {
            return AllEnginesLabel;
        }

        var label = TranslationRecord.ShortEngineName(raw);
        if (label != raw && _historyEngineRawByLabel.TryGetValue(label, out var taken) && taken != raw)
        {
            return raw;
        }

        _historyEngineRawByLabel[label] = raw;
        return label;
    }

    partial void OnHistoryTimeFilterLabelChanged(string value) =>
        HistoryTimeFilter = HistoryTimeLabelToValue(value);

    private static string HistoryTimeLabelToValue(string label) => label switch
    {
        "今天" => HistoryTimeRanges.Today,
        "近 7 天" => HistoryTimeRanges.Week,
        "近 30 天" => HistoryTimeRanges.Month,
        _ => HistoryTimeRanges.All,
    };

    /// <summary>时间胶囩的四档（设计稿只画了一枚「近 30 天」，这里补齐其余三档）。</summary>
    public IReadOnlyList<string> HistoryTimeLabels { get; } = ["全部时间", "今天", "近 7 天", "近 30 天"];

    /// <summary>检索行右端的印数；千位分隔用不受区域设置影响的空格，与设计稿 `4 812 印` 一致。</summary>
    public string HistoryCountDisplay =>
        $"{HistoryCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ")} 印";

    partial void OnHistoryCountChanged(int value) => OnPropertyChanged(nameof(HistoryCountDisplay));

    public IReadOnlyList<string> HistoryEngineOptions { get; private set; } = [AllEnginesLabel];

    public IReadOnlyList<string> HistoryTargetLanguageOptions { get; private set; } = [AllLanguagesLabel];

    partial void OnHistoryKeywordChanged(string value) => _ = RefreshHistoryAsync();


    /// <summary>供窗口在首次切到历史页时触发懒加载。</summary>
    public Task RefreshHistoryOnceAsync() => RefreshHistoryAsync();

    /// <summary>把界面上的三枚胶囊与关键字合成一次查询；刷新与导出共用，不会两边跑偏。</summary>
    private HistoryQuery BuildHistoryQuery(int limit = 500) => new()
    {
        Keyword = HistoryKeyword,
        Limit = limit,
        PinnedOnly = HistoryPinnedOnly,
        Engine = HistoryEngineFilter,
        TargetLanguage = HistoryTargetLanguageFilter,
        From = HistoryTimeRanges.StartOf(HistoryTimeFilter),
    };

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        // 版本号只负责丢弃旧结果；不做同关键字去重，保证懒加载/刷新永远会有一次真实查询。
        var version = ++_historyRefreshVersion;
        var query = BuildHistoryQuery();
        var keyword = query.Keyword;
        var grouped = HistoryGroupedView && string.IsNullOrWhiteSpace(keyword)
            && HistoryEngineFilter.Length == 0
            && HistoryTargetLanguageFilter.Length == 0
            && !HistoryPinnedOnly;

        try
        {
            // 查询与行序列构建都在后台线程：500 条记录的分组 + 摊平不该占 UI 线程
            var built = await Task.Run(() => BuildHistoryRows(query, grouped));
            if (version != _historyRefreshVersion)
            {
                return; // 已有更新的查询，旧结果作废
            }

            // 整集合一次性替换：逐条 Clear()+Add() 会触发 N 次 CollectionChanged，
            // 每次都要丢弃并重建整个列表的视觉子树
            UpdateHistoryFilterOptions(built.Records);
            HistoryRows = new ObservableCollection<HistoryRow>(built.Rows);
            HistoryCount = built.Count;

            HistoryMessage = built.Count == 0
                ? (string.IsNullOrWhiteSpace(keyword) ? "暂无翻译历史" : "没有匹配的记录")
                : $"共 {built.Count} 条"
                  + (grouped ? $" · {built.GroupCount} 个会话" : "")
                  + (built.Count >= 500 ? "（仅显示最近 500 条）" : "");
        }
        catch (Exception ex)
        {
            HistoryMessage = "历史加载失败，请点击刷新重试";
            Log.Warning(ex, "加载翻译历史失败");
        }
    }

    /// <summary>从当前历史数据聚合下拉项；不覆盖已选中的有效值，避免每次刷新丢失选择。</summary>
    private void UpdateHistoryFilterOptions(IReadOnlyList<TranslationRecord> records)
    {
        var engines = records.Select(r => r.Engine).Where(e => e.Length > 0).Distinct().Order().ToArray();
        var languages = records.Select(r => r.TargetLanguage).Where(l => l.Length > 0).Distinct().Order().ToArray();
        var engineOptions = new List<string> { AllEnginesLabel };
        _historyEngineRawByLabel.Clear();
        engineOptions.AddRange(engines.Select(EngineLabelFor));
        HistoryEngineOptions = engineOptions;
        var languageOptions = new List<string> { AllLanguagesLabel };
        languageOptions.AddRange(languages);
        HistoryTargetLanguageOptions = languageOptions;
        OnPropertyChanged(nameof(HistoryEngineOptions));
        OnPropertyChanged(nameof(HistoryTargetLanguageOptions));
        SyncFilterLabels();
    }

    /// <summary>
    /// 让胶囩文案与查询值保持一致：聚合出的选项里已经没了当前选择（例如印谱被清空），
    /// 就退回「全部」，否则胶囩会显示一个下拉里不存在的值。
    /// 直接改字段再加通知，避免 value 变更再触发一次刷新。
    /// </summary>
    private void SyncFilterLabels()
    {
        if (HistoryEngineFilter.Length > 0 && !HistoryEngineOptions.Contains(HistoryEngineFilter))
        {
            HistoryEngineFilter = "";
        }

        var engine = EngineLabelFor(HistoryEngineFilter);
        if (HistoryEngineFilterLabel != engine)
        {
            HistoryEngineFilterLabel = engine;
        }

        if (HistoryTargetLanguageFilter.Length > 0
            && !HistoryTargetLanguageOptions.Contains(HistoryTargetLanguageFilter))
        {
            HistoryTargetLanguageFilter = "";
        }

        var language = HistoryTargetLanguageFilter.Length == 0
            ? AllLanguagesLabel
            : HistoryTargetLanguageFilter;
        if (HistoryTargetLanguageFilterLabel != language)
        {
            HistoryTargetLanguageFilterLabel = language;
        }
    }

    [RelayCommand]
    private async Task TogglePinHistoryAsync(TranslationRecord? record)
    {
        if (record is null)
        {
            return;
        }

        _history.SetPinned(record.Id, !record.Pinned);
        await RefreshHistoryAsync();
    }

    private sealed record BuiltHistory(List<HistoryRow> Rows, IReadOnlyList<TranslationRecord> Records, int Count, int GroupCount);

    private BuiltHistory BuildHistoryRows(HistoryQuery query, bool grouped)
    {
        var records = _history.Search(query);
        var keyword = query.Keyword;
        var rows = new List<HistoryRow>(records.Count + 16);

        if (!grouped)
        {
            foreach (var record in records)
            {
                rows.Add(new HistoryRecordRow(record));
            }

            return new BuiltHistory(rows, records, records.Count, 0);
        }

        var groups = HistoryGrouper.Group(records);
        for (var index = 0; index < groups.Count; index++)
        {
            rows.Add(new HistoryHeaderRow(index, groups[index].Label, "收起"));
            foreach (var record in groups[index].Records)
            {
                rows.Add(new HistoryRecordRow(record));
            }
        }

        return new BuiltHistory(rows, records, records.Count, groups.Count);
    }

    /// <summary>折叠/展开一个会话组（FR-057）：只从行序列里摘掉/放回该组的记录行。</summary>
    [RelayCommand]
    private void ToggleHistoryGroup(HistoryHeaderRow? header)
    {
        if (header is null)
        {
            return;
        }

        var collapsed = !_collapsedGroups.Contains(header.GroupIndex);
        if (collapsed)
        {
            _collapsedGroups.Add(header.GroupIndex);
        }
        else
        {
            _collapsedGroups.Remove(header.GroupIndex);
        }

        var rows = new List<HistoryRow>(HistoryRows.Count);
        var current = -1;
        foreach (var row in HistoryRows)
        {
            switch (row)
            {
                case HistoryHeaderRow h:
                    current = h.GroupIndex;
                    rows.Add(h with { ToggleText = h.GroupIndex == header.GroupIndex
                        ? (collapsed ? "展开" : "收起") : h.ToggleText });
                    break;
                case HistoryRecordRow when current == header.GroupIndex && collapsed:
                    break;
                default:
                    rows.Add(row);
                    break;
            }
        }

        HistoryRows = new ObservableCollection<HistoryRow>(rows);
    }

    [RelayCommand]
    private async Task DeleteHistoryAsync(TranslationRecord? record)
    {
        if (record is null)
        {
            return;
        }

        _history.Delete(record.Id);
        await RefreshHistoryAsync();
    }

    [RelayCommand]
    private void CopyHistory(TranslationRecord? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            // 抑制剪贴板监听，避免本程序的写入被当作「用户复制」而触发翻译（FR-017）
            _clipboardMonitor.Suppress(TimeSpan.FromSeconds(1));
            Clipboard.SetText($"{record.SourceText}\n{record.TranslatedText}");
            HistoryMessage = "已复制该条记录";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制历史记录失败");
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        // FR-014：一键清空需二次确认
        var confirm = MessageBox.Show(
            $"确定要清空全部 {HistoryCount} 条翻译历史吗？此操作不可撤销。",
            "译印 · 清空历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _history.Clear();
        await RefreshHistoryAsync();
        HistoryMessage = "已清空翻译历史";
    }

    /// <summary>导出当前筛选下的印谱（FR-014）：CSV 给表格看，TSV 直接丢给 Anki。</summary>
    [RelayCommand]
    private void ExportHistoryCsv() => ExportHistory("CSV 文件|*.csv", "印谱.csv", anki: false);

    [RelayCommand]
    private void ExportHistoryTsv() => ExportHistory("TSV 文件（Anki 可导入）|*.tsv", "印谱-anki.tsv", anki: true);

    private void ExportHistory(string filter, string defaultName, bool anki)
    {
        // 导出的是「这一次筛选的结果」，不是整库：所见即所得，不会把用户看不到的记录写进去。
        var records = _history.Search(BuildHistoryQuery(HistoryRepository.MaxRecords));
        if (records.Count == 0)
        {
            HistoryMessage = "当前筛选下没有可导出的记录";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var builder = new System.Text.StringBuilder();
            if (!anki)
            {
                builder.AppendLine("时间,引擎,源语言,目标语言,原文,译文,已校对,禁用复用,已固定");
            }

            foreach (var record in records)
            {
                // Anki 只要正面/背面两列；CSV 带上全部元数据，方便再加工
                var cells = anki
                    ? new[] { record.SourceText, record.TranslatedText }
                    : new[]
                    {
                        record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        record.Engine,
                        record.SourceLanguage,
                        record.TargetLanguage,
                        record.SourceText,
                        record.TranslatedText,
                        record.Reviewed ? "是" : "否",
                        record.Rejected ? "是" : "否",
                        record.Pinned ? "是" : "否",
                    };

                builder.AppendLine(string.Join(anki ? '\t' : ',', cells.Select(c => EscapeCell(c, anki))));
            }

            // CSV 带 BOM 便于 Excel 正确识别中文；TSV 为 Anki 导入用，不加 BOM
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: !anki);
            File.WriteAllText(dialog.FileName, builder.ToString(), encoding);
            HistoryMessage = $"已导出 {records.Count} 条到 {Path.GetFileName(dialog.FileName)}";

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出印谱失败");
            HistoryMessage = "导出失败，请检查目标路径是否可写";
        }
    }

    /// <summary>单元格转义：记录是任意文本，换行要压平，CSV 里的逗号与引号要括起来。</summary>
    private static string EscapeCell(string value, bool anki)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (anki)
        {
            return flat;
        }

        return flat.Contains(',') || flat.Contains('"')
            ? $"\"{flat.Replace("\"", "\"\"")}\""
            : flat;
    }

    // ==================== FR-015 生词本 ====================


    [ObservableProperty]
    private ObservableCollection<VocabularyEntry> _vocabularyItems = [];

    /// <summary>「已藏」分组头上的枚数（设计稿 .group-h 的「已藏 · 128 枚」）。</summary>
    public string VocabularyGroupTitle => $"已藏 · {VocabularyItems.Count} 枚";

    partial void OnVocabularyItemsChanged(ObservableCollection<VocabularyEntry> value) =>
        OnPropertyChanged(nameof(VocabularyGroupTitle));

    [ObservableProperty]
    private string _vocabularyMessage = "";

    [RelayCommand]
    private void RefreshVocabulary()
    {
        // 整集合一次性替换：避免逐条 Clear()+Add() 触发 N 次视觉树重建
        VocabularyItems = new ObservableCollection<VocabularyEntry>(_vocabulary.List());
        VocabularyMessage = VocabularyItems.Count == 0
            ? "生词本为空：在翻译小窗点击收藏按钮即可加入"
            : $"共 {VocabularyItems.Count} 个词条";
    }

    /// <summary>仅供 --render-ui：直接摆几条样例进列表，不落库（否则存词行永远只能拍到空态）。</summary>
    internal void SeedVocabularyForRender()
    {
        var now = DateTimeOffset.Now;
        VocabularyItems =
        [
            new VocabularyEntry(1, now.AddDays(-3), "serendipity", "意外之喜；不期而遇的美好", "en", "zh-CN"),
            new VocabularyEntry(2, now.AddDays(-5), "断舍离", "Letting go of what does not spark joy", "zh-CN", "en"),
            new VocabularyEntry(3, now.AddDays(-9), "Zeitgeist", "时代精神；时代思潮", "de", "zh-CN"),
        ];
        VocabularyMessage = $"共 {VocabularyItems.Count} 个词条";
    }

    [RelayCommand]
    private void DeleteVocabulary(VocabularyEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        _vocabulary.Delete(entry.Id);
        RefreshVocabulary();
    }

    [RelayCommand]
    private void ExportVocabularyCsv() => ExportVocabulary(
        _vocabulary.ExportCsv(), "CSV 文件|*.csv", "生词本.csv");

    [RelayCommand]
    private void ExportVocabularyTsv() => ExportVocabulary(
        _vocabulary.ExportTsv(), "TSV 文件（Anki 可导入）|*.tsv", "生词本-anki.tsv");

    private void ExportVocabulary(string content, string filter, string defaultName)
    {
        if (VocabularyItems.Count == 0)
        {
            VocabularyMessage = "生词本为空，无法导出";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // CSV 带 BOM 便于 Excel 正确识别中文；TSV 为 Anki 导入用，不加 BOM
            var encoding = filter.Contains("CSV", StringComparison.Ordinal)
                ? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                : new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(dialog.FileName, content, encoding);
            VocabularyMessage = $"已导出 {VocabularyItems.Count} 个词条到 {Path.GetFileName(dialog.FileName)}";

            // 导出后为便于用户确认，打开所在目录（不启动外部程序打开文件本身）
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出词汇失败");
            VocabularyMessage = "导出失败，请检查目标路径是否可写";
        }
    }

    // ==================== FR-016 朗读 / FR-017 剪贴板监听 ====================

    [ObservableProperty]
    private bool _clipboardMonitorEnabled;

    [ObservableProperty]
    private bool _autoSpeakAfterSelect;

    private readonly bool _ttsAvailable;

    /// <summary>系统是否具备语音包（无则禁用自动朗读并提示）。</summary>
    public bool TtsAvailable => _ttsAvailable;

    public string TtsHint => _ttsAvailable
        ? "使用 Windows 系统语音，无需联网"
        : "系统未安装语音包，请在「设置 → 时间和语言 → 语音」中添加后重启本程序";

    partial void OnClipboardMonitorEnabledChanged(bool value)
    {
        Save(s => s.ClipboardMonitorEnabled = value);
        if (value)
        {
            _clipboardMonitor.Start();
        }
        else
        {
            _clipboardMonitor.Stop();
        }
    }

    // ==================== P0 批 1：隐私模式 / 自动整理换行 / 引擎看板 ====================

    /// <summary>隐私模式（spec §2）：本地留痕全关；翻译请求本身仍会发送。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverLockedByPrivacy))]
    private bool _privacyMode;

    /// <summary>自动整理换行（spec §3）：划词/剪贴板文本合并硬换行，默认开。</summary>
    [ObservableProperty]
    private bool _cleanClipboardText;

    /// <summary>
    /// 悬停取词（FR-036 / spec §2.3）：默认关——它需要常驻低级鼠标钩子。
    /// 隐私模式开启时开关可开但钩子**不装**，卡片用 <see cref="HoverLockedByPrivacy"/> 说明原因。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverLockedByPrivacy))]
    private bool _hoverSelectEnabled;

    /// <summary>术语表全局开关（FR-037 / spec §4）：关闭 = 词条保留但不参与替换。</summary>
    [ObservableProperty]
    private bool _glossaryEnabled;

    /// <summary>悬停开关被隐私模式压住（UI 显示「钩子未启用」说明）。</summary>
    public bool HoverLockedByPrivacy => PrivacyMode && HoverSelectEnabled;

    partial void OnHoverSelectEnabledChanged(bool value)
    {
        Save(s => s.HoverSelectEnabled = value);
        ApplyHookGates();
    }

    partial void OnGlossaryEnabledChanged(bool value) => Save(s => s.GlossaryEnabled = value);

    // ==================== P0 批 3：双击修饰键 / 鼠标侧键 / 粘贴即译 ====================

    /// <summary>双击修饰键划词（FR-038，默认关）：需要常驻键盘钩子，隐私模式开启时绝不安装。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoubleTapLockedByPrivacy))]
    private bool _doubleTapTranslateEnabled;

    /// <summary>双击的目标修饰键：alt / ctrl / shift / win（检测器每次现读，改完即生效）。</summary>
    [ObservableProperty]
    private string _doubleTapKey = "alt";

    /// <summary>X1 后退键 = 划词翻译（FR-039，默认关）。</summary>
    [ObservableProperty]
    private bool _mouseSideButtonSelect;

    /// <summary>X2 前进键 = 截图翻译（FR-039，默认关）。</summary>
    [ObservableProperty]
    private bool _mouseSideButtonCapture;

    /// <summary>粘贴即译（FR-040，默认开）：小窗输入框为空时 Ctrl+V 直接翻译剪贴板。</summary>
    [ObservableProperty]
    private bool _pasteTranslateEnabled;

    /// <summary>TM 相似句回填（FR-045，默认开）：近似句直接复用历史译文，可一键重译。</summary>
    [ObservableProperty]
    private bool _tmReuseEnabled;

    partial void OnTmReuseEnabledChanged(bool value) => Save(s => s.TmReuseEnabled = value);

    /// <summary>
    /// AI 语境化（FR-050，默认开）：把同语言对 30 分钟内最近一条原文写进系统提示，让连续翻译保持术语与语气连贯。
    /// 只对 AI 引擎有效；隐私模式下历史不入库 → 自然无语境，也不会因此多送一个字出网。
    /// </summary>
    [ObservableProperty]
    private bool _llmContextEnabled;

    partial void OnLlmContextEnabledChanged(bool value) => Save(s => s.LlmContextEnabled = value);

    /// <summary>一条已记住的「应用 → 语言对」规则（FR-058 设置页展示用）。</summary>
    public sealed class AppLanguageRuleRow
    {
        public required string Process { get; init; }
        public required string Display { get; init; }
    }

    public ObservableCollection<AppLanguageRuleRow> AppLanguageRuleRows { get; } = [];

    /// <summary>有没有记住过规则（决定卡片里显示空态提示还是列表）。</summary>
    public bool HasAppLanguageRules => AppLanguageRuleRows.Count > 0;

    public bool HasNoAppLanguageRules => !HasAppLanguageRules;

    /// <summary>按应用记忆语言对（FR-058，默认关）：命中只改本次会话，不改全局默认。</summary>
    [ObservableProperty]
    private bool _appLanguageMemoryEnabled;

    partial void OnAppLanguageMemoryEnabledChanged(bool value) => Save(s => s.AppLanguageMemoryEnabled = value);

    /// <summary>
    /// 再看一次上手卡（FR-059）：窗口用完即关、不可复用，所以每次直接新建。
    /// 显示失败不打扰用户（它只是个提示卡，不该挡住任何操作）。
    /// </summary>
    [RelayCommand]
    private void ShowOnboarding()
    {
        try
        {
            var guide = new TranslationApp.Windows.FirstRunGuideWindow(
                _settings, _store, _hotkeyManager, _catalog, _ocr, _inPlaceTranslations, _autoStart);
            guide.OpenDoctorRequested += (_, _) => SettingsNavigationRequested?.Invoke(this, "诊断");
            guide.Show();
            guide.Activate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "上手卡显示失败（忽略）");
        }
    }

    private void RefreshAppLanguageRules()
    {
        AppLanguageRuleRows.Clear();
        foreach (var rule in _settings.AppLanguagePairs)
        {
            AppLanguageRuleRows.Add(new AppLanguageRuleRow
            {
                Process = rule.Process,
                Display = $"{rule.Process}：{TranslationLanguages.DisplayName(rule.SourceLanguage)} → "
                          + $"{TranslationLanguages.DisplayName(rule.TargetLanguage)}",
            });
        }

        OnPropertyChanged(nameof(HasAppLanguageRules));
        OnPropertyChanged(nameof(HasNoAppLanguageRules));
    }

    [RelayCommand]
    private void ForgetAppLanguageRule(string? process)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return;
        }

        _settings.AppLanguagePairs = AppLanguageRules.Forget(_settings.AppLanguagePairs, process).ToList();
        Save(s => s.AppLanguagePairs = _settings.AppLanguagePairs);
        RefreshAppLanguageRules();
    }

    [RelayCommand]
    private void ClearAppLanguageRules()
    {
        _settings.AppLanguagePairs = [];
        Save(s => s.AppLanguagePairs = _settings.AppLanguagePairs);
        RefreshAppLanguageRules();
    }

    /// <summary>每日复习 5 词（FR-052，默认关）：纯轮转提醒，不统计熟悉度。</summary>
    [ObservableProperty]
    private bool _dailyReviewEnabled;

    partial void OnDailyReviewEnabledChanged(bool value) => Save(s => s.DailyReviewEnabled = value);

    /// <summary>影子跟读入口（FR-053，默认关）：开启后译文区出现「跟读」按钮。</summary>
    [ObservableProperty]
    private bool _shadowReadingEnabled;

    partial void OnShadowReadingEnabledChanged(bool value) => Save(s => s.ShadowReadingEnabled = value);

    /// <summary>跟读句间停顿毫秒（夹在 200~3000，避免填 0 变成连读、填超大值像卡死）。</summary>
    [ObservableProperty]
    private int _shadowPauseMs;

    partial void OnShadowPauseMsChanged(int value)
    {
        var clamped = Math.Clamp(value, 200, 3000);
        Save(s => s.ShadowPauseMs = clamped);
        if (clamped != value)
        {
            // 用户填了区间外的值：回写夹取结果，输入框与设置保持一致（否则显示与生效值两样）
            ShadowPauseMs = clamped;
        }
    }

    /// <summary>目标修饰键下拉项。</summary>
    public IReadOnlyList<ThemeOption> DoubleTapKeyOptions { get; } =
    [
        new("alt", "Alt"),
        new("ctrl", "Ctrl"),
        new("shift", "Shift"),
        new("win", "Win"),
    ];

    /// <summary>双击开关被隐私模式压住（卡片显示说明行）。</summary>
    public bool DoubleTapLockedByPrivacy => PrivacyMode && DoubleTapTranslateEnabled;

    private static string NormalizeDoubleTapKey(string? key) => key switch
    {
        "ctrl" or "shift" or "win" => key,
        _ => "alt",
    };

    partial void OnDoubleTapTranslateEnabledChanged(bool value)
    {
        Save(s => s.DoubleTapTranslateEnabled = value);
        ApplyHookGates();
    }

    partial void OnDoubleTapKeyChanged(string value) =>
        Save(s => s.DoubleTapKey = NormalizeDoubleTapKey(value));

    partial void OnMouseSideButtonSelectChanged(bool value)
    {
        Save(s => s.MouseSideButtonSelect = value);
        ApplyHookGates();
    }

    partial void OnMouseSideButtonCaptureChanged(bool value)
    {
        Save(s => s.MouseSideButtonCapture = value);
        ApplyHookGates();
    }

    partial void OnPasteTranslateEnabledChanged(bool value) => Save(s => s.PasteTranslateEnabled = value);

    /// <summary>
    /// 两个全局钩子的统一门控（与 App.ApplyPrivacySideEffects 同一条规则）：
    /// 隐私模式开启 → 全停；否则鼠标钩子按「悬停/侧键任一开」、键盘钩子按「双击开关」启停。
    /// </summary>
    private void ApplyHookGates()
    {
        if (App.MouseHookNeeded(_settings) && !_settings.PrivacyMode)
        {
            _mouseHook.Start();
        }
        else
        {
            _mouseHook.Stop();
        }

        if (_settings.DoubleTapTranslateEnabled && !_settings.PrivacyMode)
        {
            _keyboardHook.Start();
        }
        else
        {
            _keyboardHook.Stop();
        }

        // 这条日志是唯一的可观测点：开关在设置页改动时不写日志，就无法区分「没装」与「装了但收不到事件」
        Log.Information("全局钩子门控：键盘 {Keyboard}，鼠标 {Mouse}",
            HookState(_keyboardHook.IsActive, _keyboardHook.LastStartError),
            HookState(_mouseHook.IsActive, _mouseHook.LastStartError));
    }

    private static string HookState(bool active, int error) => active ? "已安装"
        : error == 0 ? "已停用" : $"安装失败(Win32 {error})";

    partial void OnPrivacyModeChanged(bool value)
    {
        Save(s => s.PrivacyMode = value);
        // 与 App 托盘菜单同一套副作用：开启即停剪贴板监听，关闭且用户开了监听再启动
        if (value)
        {
            _clipboardMonitor.Stop();
        }
        else if (_settings.ClipboardMonitorEnabled)
        {
            _clipboardMonitor.Start();
        }

        ApplyHookGates(); // 批 2/3：隐私模式联动两个全局钩子（B5 红线）
        // 批 4：本地 API 与钩子同纪律——隐私开即停监听，关则按设置恢复
        _localApiGateway.Apply(_settings.LocalApiEnabled && !value);
        UpdateLocalApiStatus();
    }

    partial void OnCleanClipboardTextChanged(bool value) => Save(s => s.CleanClipboardText = value);
    partial void OnProfileStatusTextChanged(string value) => RefreshProfileStatusOverride();

    /// <summary>让状态行在受限文件继续使用旧短语时，也能按 ProfileOverrides 的 9 键展示实际改动项。</summary>
    private void RefreshProfileStatusOverride()
    {
        if (!ProfileStatusText.StartsWith("当前：", StringComparison.Ordinal))
        {
            return;
        }

        var profile = _profiles.AllProfiles().FirstOrDefault(p => p.Name == _profiles.DisplayName);
        ProfileStatusText = profile is null
            ? "当前：标准 · 使用当前配置"
            : $"当前：{_profiles.DisplayName} · {DescribeOverrides(profile.Overrides)}"
              + (_profiles.IsDeviation() ? "（当前设置已偏离）" : "");
    }

    /// <summary>把当前模式实际会改的项展开成一句话，避免用户只看到“打包成一档”却不知道它动了什么。</summary>
    private string DescribeOverrides(ProfileOverrides overrides)
    {
        var items = new List<string>();
        if (overrides.Engine is { } engine)
        {
            items.Add($"AI 引擎：{_catalog.Find(engine)?.Name ?? engine}");
        }

        if (overrides.SourceLanguage is { } source)
        {
            items.Add($"原文：{TranslationLanguages.DisplayName(source)}");
        }

        if (overrides.TargetLanguage is { } target)
        {
            items.Add($"译文：{TranslationLanguages.DisplayName(target)}");
        }

        if (overrides.Style is { } style)
        {
            items.Add($"风格：{TranslationStyles.Parse(style).DisplayName()}");
        }

        if (overrides.CleanClipboardText is { } clean)
        {
            items.Add($"自动整理换行：{(clean ? "开" : "关")}");
        }

        if (overrides.PrivacyMode is { } privacy)
        {
            items.Add($"隐私模式：{(privacy ? "开" : "关")}");
        }

        if (overrides.GlossaryEnabled is { } glossary)
        {
            items.Add($"术语表：{(glossary ? "开" : "关")}");
        }

        if (overrides.ClipboardMonitorEnabled is { } monitor)
        {
            items.Add($"剪贴板监听：{(monitor ? "开" : "关")}");
        }

        if (overrides.HoverSelectEnabled is { } hover)
        {
            items.Add($"悬停取词：{(hover ? "开" : "关")}");
        }

        return items.Count == 0 ? "使用当前配置" : string.Join(" / ", items);
    }

    /// <summary>切到「引擎」页时刷新各卡近 7 天看板。</summary>
    public void RefreshEngineStats()
    {
        foreach (var card in EngineCards)
        {
            card.RefreshStats();
        }

        AiProviders?.RefreshStats();
    }

    partial void OnAutoSpeakAfterSelectChanged(bool value) => Save(s => s.AutoSpeakAfterSelect = value);

    // ==================== FR-018 网络代理 ====================

    [ObservableProperty]
    private bool _proxyEnabled;

    [ObservableProperty]
    private string _selectedProxyMode;

    [ObservableProperty]
    private string _proxyHost;

    [ObservableProperty]
    private string _proxyScheme;

    /// <summary>代理协议选项（FR-018 要求支持 HTTP 与 SOCKS5）。</summary>
    public IReadOnlyList<ProxyModeOption> ProxySchemeOptions { get; } =
    [
        new("http", "HTTP"),
        new("socks5", "SOCKS5"),
    ];

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private string _proxyUserName;

    [ObservableProperty]
    private string _proxyPassword;

    [ObservableProperty]
    private string _proxyMessage = "";

    partial void OnProxyEnabledChanged(bool value) => SaveProxy(s => s.ProxyEnabled = value);

    partial void OnSelectedProxyModeChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            SaveProxy(s => s.ProxyMode = value);
        }
    }

    partial void OnProxyHostChanged(string value) => SaveProxy(s => s.ProxyHost = value.Trim());

    partial void OnProxySchemeChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            SaveProxy(s => s.ProxyScheme = NormalizeProxyScheme(value));
        }
    }

    partial void OnProxyPortChanged(int value) => SaveProxy(s => s.ProxyPort = value);

    partial void OnProxyUserNameChanged(string value) => SaveProxy(s => s.ProxyUserName = value.Trim());

    partial void OnProxyPasswordChanged(string value)
    {
        if (_suppressPasswordCallback)
        {
            return;
        }

        // 密码经 DPAPI 加密后落盘，配置文件中不存在明文（FR-010/018）
        SaveProxy(s => s.ProxyPasswordEncrypted = SecretStore.Protect(value));
    }

    private void SaveProxy(Action<AppSettings> apply)
    {
        Save(apply);
        ProxyMessage = "";
    }

    [RelayCommand]
    private async Task TestProxyAsync()
    {
        if (string.IsNullOrWhiteSpace(ProxyHost) || ProxyPort is <= 0 or > 65535)
        {
            ProxyMessage = "请先填写有效的代理地址与端口";
            return;
        }

        ProxyMessage = "正在测试…";
        var password = ProxyPassword ?? "";
        var options = new ProxyOptions(ProxyHost.Trim(), ProxyPort, ProxyUserName?.Trim(), password, ProxyScheme);

        try
        {
            using var client = TranslationHttpClientFactory.Create(options.CreateWebProxy());
            client.Timeout = TimeSpan.FromSeconds(12);
            // 直接探测 Google 翻译端点：开代理的目的就是让国外引擎可达，
            // 用国内可达地址测只能证明代理本身能用，证明不了国外引擎能通。
            using var response = await client.GetAsync(
                "https://translate.googleapis.com/translate_a/single" +
                "?client=dict-chrome-ex&dt=t&sl=auto&tl=zh-CN&q=hello");

            ProxyMessage = (int)response.StatusCode switch
            {
                200 => "代理可用，Google 引擎可达",
                429 => "代理可用，但 Google 限流（HTTP 429），可稍后重试或改用其他引擎",
                403 => "代理可用，但 Google 拒绝了请求（HTTP 403）",
                var code => $"代理已连通，Google 返回 HTTP {code}",
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "代理测试失败");
            ProxyMessage = "代理不可用：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task EditHistoryAsync(TranslationRecord? record)
    {
        if (record is null) return;

        var dialog = new TranslationApp.Windows.EditTranslationWindow(record)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dialog.EditedText))
        {
            HistoryMessage = "译文不能为空";
            return;
        }

        _history.Update(record.Id, dialog.EditedText.Trim(), dialog.Reviewed && !dialog.Rejected, dialog.Rejected);
        if (dialog.AddToGlossary) AddGlossaryFromHistory(
            record.SourceText, dialog.EditedText.Trim(), record.SourceLanguage, record.TargetLanguage);
        await RefreshHistoryAsync();
        HistoryMessage = dialog.AddToGlossary ? "译文已保存并加入术语表" : "译文已保存";
    }

    [RelayCommand]
    private async Task ToggleHistoryReviewedAsync(TranslationRecord? record)
    {
        if (record is null) return;
        var reviewed = !record.Reviewed;
        _history.Update(record.Id, record.TranslatedText, reviewed, reviewed ? false : record.Rejected);
        await RefreshHistoryAsync();
    }

    [RelayCommand]
    private async Task ToggleHistoryRejectedAsync(TranslationRecord? record)
    {
        if (record is null) return;
        var rejected = !record.Rejected;
        _history.Update(record.Id, record.TranslatedText, rejected ? false : record.Reviewed, rejected);
        await RefreshHistoryAsync();
    }

    [RelayCommand]
    private void AddHistoryToGlossary(TranslationRecord? record)
    {
        if (record is null) return;
        AddGlossaryFromHistory(record.SourceText, record.TranslatedText, record.SourceLanguage, record.TargetLanguage);
        HistoryMessage = "已加入术语表";
    }

    private void AddGlossaryFromHistory(
        string source, string target, string sourceLanguage = "*", string targetLanguage = "*")
    {
        if (GlossaryItems.Any(item =>
                string.Equals(item.Source, source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Target, target, StringComparison.Ordinal)))
        {
            return;
        }

        GlossaryItems.Add(new GlossaryItemViewModel
        {
            Source = source,
            Target = target,
            Enabled = true,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
        });
        SaveGlossary(quiet: false);
    }

    // ==================== 公共 ====================

    internal static AppTheme ToAppTheme(string? value) => value switch
    {
        "auto" => AppTheme.Auto,
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    private static string NormalizeTheme(string? value) =>
        value is "light" or "dark" or "auto" ? value : "system";

    private static string NormalizeProxyMode(string? value) =>
        string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "googleOnly";

    /// <summary>代理协议只接受 http / socks5，其余按 http 处理（含旧配置缺该字段的情况）。</summary>
    private static string NormalizeProxyScheme(string? value) =>
        string.Equals(value, "socks5", StringComparison.OrdinalIgnoreCase) ? "socks5" : "http";

    private void Save(Action<AppSettings> apply)
    {
        apply(_settings);
        _store.Save(_settings);
    }
}
