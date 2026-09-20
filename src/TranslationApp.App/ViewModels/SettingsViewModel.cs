using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using TranslationApp.Core.Anki;
using TranslationApp.Core.History;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Layout;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Services;
using TranslationApp.Theming;
// 别名：本类会生成同名属性 OcrOutputMode，直接用类型名会被属性遮蔽（FR-027）
using OcrMode = TranslationApp.Core.Capture.OcrOutputMode;

namespace TranslationApp.ViewModels;

/// <summary>主题下拉项。</summary>
public sealed record ThemeOption(string Value, string Display);

/// <summary>代理作用范围下拉项（FR-018）。</summary>
public sealed record ProxyModeOption(string Value, string Display);

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
    private bool _suppressAutoStartCallback;
    private bool _suppressPasswordCallback;

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
        TranslationApp.Core.Dictionary.DictionaryManager? dictionaries = null)
    {
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
        _dictionaries = dictionaries;

        _showStartBalloon = settings.ShowStartBalloon;
        _autoStartEnabled = autoStart.IsEnabled;
        _targetLanguage = settings.TargetLanguage;
        _hotkeyInputText = settings.HotkeyInputTranslate;
        _hotkeySelectText = settings.HotkeySelectTranslate;
        _hotkeyCaptureText = settings.HotkeyCaptureTranslate;
        _hotkeyProfileText = settings.HotkeySwitchProfile;
        _selectedTheme = NormalizeTheme(settings.Theme);

        // 「引擎」页（FR-024）先建卡片，再决定当前引擎是否可用（未配置则回退并提示）
        InitializeEnginePage();
        _selectedEngine = catalog.Resolve(settings.Engine).Id;

        _clipboardMonitorEnabled = settings.ClipboardMonitorEnabled;
        _autoSpeakAfterSelect = settings.AutoSpeakAfterSelect;
        // P0 批 1：隐私模式 / 阅读清洗 / 术语表
        _privacyMode = settings.PrivacyMode;
        _cleanClipboardText = settings.CleanClipboardText;
        InitializeGlossaryPage();
        // P0 批 2：悬停取词 / 术语全局开关 / Anki 直推 / 场景档案
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
        // P0 批 4：本地 HTTP API（状态行需反映监听实况，构造时刷一次）
        InitializeApiPage();
        // P0 批 4：本地 mdx 词典（列表要显示「装了但解析不了」的项，构造时扫一次）
        InitializeDictionariesPage();

        // FR-026「通用 → 小窗尺寸」：默认宽高即设置里的 QuickWindowWidth/Height（见 AppSettings 注释），
        // 开关沿用 QuickWindowSizeMode（auto = 按内容自适应 / manual = 固定用默认宽高）
        _quickWindowAutoSize = !WindowSizePolicy.IsManual(settings.QuickWindowSizeMode);
        _quickWindowDefaultWidth = WindowSizePolicy.ClampWidth(settings.QuickWindowWidth);
        _quickWindowDefaultHeight = WindowSizePolicy.ClampHeight(settings.QuickWindowHeight);
        UpdateQuickWindowSizeMessage();

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

        RefreshHistory();
        RefreshVocabulary();
        if (_clipboardMonitorEnabled)
        {
            _clipboardMonitor.Start();
        }
    }

    // ==================== 通用 ====================

    public IReadOnlyList<TranslationLanguages.LanguageOption> TargetOptions => TranslationLanguages.TargetOptions;

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new("system", "跟随系统"),
        new("light", "浅色"),
        new("dark", "深色"),
    ];

    public IReadOnlyList<ProxyModeOption> ProxyModeOptions { get; } =
    [
        new("googleOnly", "仅国外引擎（Google / Azure / DeepL / AI）"),
        new("all", "全部引擎"),
    ];

    [ObservableProperty]
    private bool _showStartBalloon;

    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private string _targetLanguage;

    [ObservableProperty]
    private string _selectedTheme;

    [ObservableProperty]
    private string _selectedEngine;

    [ObservableProperty]
    private string _hotkeyInputText;

    [ObservableProperty]
    private string _hotkeySelectText;

    /// <summary>FR-021：截图翻译热键（默认 Alt+O）。</summary>
    [ObservableProperty]
    private string _hotkeyCaptureText;

    /// <summary>FR-037（批 2）：场景档案循环切换热键（默认 Alt+P）。</summary>
    [ObservableProperty]
    private string _hotkeyProfileText;

    [ObservableProperty]
    private bool _hotkeyInputInvalid;

    [ObservableProperty]
    private bool _hotkeySelectInvalid;

    [ObservableProperty]
    private bool _hotkeyCaptureInvalid;

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
                UpdateQuickWindowSizeMessage();
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
                UpdateQuickWindowSizeMessage();
            }
        }
    }

    /// <summary>当前尺寸设置的一句话说明。</summary>
    [ObservableProperty]
    private string _quickWindowSizeMessage = "";

    partial void OnQuickWindowAutoSizeChanged(bool value)
    {
        Save(s => s.QuickWindowSizeMode = value ? WindowSizePolicy.AutoMode : WindowSizePolicy.ManualMode);
        UpdateQuickWindowSizeMessage();
        Log.Information("小窗尺寸模式已切换为 {Mode}", value ? "按内容自适应" : "固定使用默认宽高");
    }

    /// <summary>「恢复推荐默认值」：宽高重置为 420 × 320（开关状态不变）。</summary>
    [RelayCommand]
    private void RestoreRecommendedDefaultSize()
    {
        QuickWindowDefaultWidth = WindowSizePolicy.DefaultWidthDip;
        QuickWindowDefaultHeight = WindowSizePolicy.DefaultHeightDip;
        QuickWindowSizeMessage = $"已恢复推荐默认值：{WindowSizePolicy.DefaultWidthDip:0} × {WindowSizePolicy.DefaultHeightDip:0} DIP";
    }

    private void UpdateQuickWindowSizeMessage()
    {
        var width = Math.Round(_quickWindowDefaultWidth);
        var height = Math.Round(_quickWindowDefaultHeight);
        QuickWindowSizeMessage = QuickWindowAutoSize
            ? $"每次呼出都用默认 {width} × {height} DIP 起算：内容多时高度自动增长"
              + "，宽度按内容最长行加宽、最多加宽到 640（默认宽度本身超过 640 时以默认值为准）；只增不减；"
              + "拖动边缘只影响本次窗口——想固化就在小窗空白处右键「设为默认尺寸」"
            : $"固定尺寸：每次呼出都是 {width} × {height} DIP，不看内容；拖动边缘只影响本次窗口";
    }

    partial void OnShowStartBalloonChanged(bool value) => Save(s => s.ShowStartBalloon = value);

    partial void OnSelectedThemeChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        Save(s => s.Theme = value);
        ThemeManager.Apply(ToAppTheme(value));
    }

    partial void OnSelectedEngineChanged(string value)
    {
        if (!string.IsNullOrEmpty(value) && _settings.Engine != value)
        {
            Save(s => s.Engine = value);
            GeneralMessage = "";
            EngineMessage = ""; // 用户已主动切换，回退提示不再适用
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
    }

    partial void OnHotkeyInputTextChanged(string value) => ApplyHotkey(HotkeySlot.Input, value);

    partial void OnHotkeySelectTextChanged(string value) => ApplyHotkey(HotkeySlot.Select, value);

    partial void OnHotkeyCaptureTextChanged(string value) => ApplyHotkey(HotkeySlot.Capture, value);

    partial void OnHotkeyProfileTextChanged(string value) => ApplyHotkey(HotkeySlot.Profile, value);

    [RelayCommand]
    private void ResetHotkeys()
    {
        ApplyHotkey(HotkeySlot.Input, HotkeyDefinition.DefaultInput.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Select, HotkeyDefinition.DefaultSelect.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Capture, HotkeyDefinition.DefaultCapture.ToString(), force: true);
        ApplyHotkey(HotkeySlot.Profile, HotkeyDefinition.DefaultProfile.ToString(), force: true);
        HotkeyInputText = HotkeyDefinition.DefaultInput.ToString();
        HotkeySelectText = HotkeyDefinition.DefaultSelect.ToString();
        HotkeyCaptureText = HotkeyDefinition.DefaultCapture.ToString();
        HotkeyProfileText = HotkeyDefinition.DefaultProfile.ToString();
    }

    private void ApplyHotkey(HotkeySlot slot, string value, bool force = false)
    {
        var current = CurrentHotkeyText(slot);
        if (!force && string.Equals(current, value, StringComparison.Ordinal) && _hotkeyManager.IsRegistered(NameOf(slot)))
        {
            return;
        }

        if (!HotkeyDefinition.TryParse(value, out var newDefinition))
        {
            SetInvalid(slot, true);
            HotkeyMessage = "热键格式不正确：需为 修饰键 + 字母/数字/F1~F12 的组合";
            return;
        }

        // 三个热键之间不得重复
        foreach (var other in OtherSlots(slot))
        {
            if (HotkeyDefinition.TryParse(CurrentHotkeyText(other), out var otherDefinition)
                && otherDefinition == newDefinition)
            {
                SetInvalid(slot, true);
                SetInvalid(other, true);
                HotkeyMessage = "热键不能重复，请重新设置";
                return;
            }
        }

        var oldDefinition = HotkeyDefinition.ParseOrDefault(current, DefaultOf(slot));

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

            HotkeyMessage = "";
            return;
        }

        _hotkeyManager.TryRegister(NameOf(slot), oldDefinition);
        SetInvalid(slot, true);
        HotkeyMessage = $"热键 {newDefinition} 注册失败，可能已被其他程序占用，已保留原热键 {oldDefinition}";
        SetHotkeyText(slot, oldDefinition.ToString());
    }

    private static string NameOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => "input",
        HotkeySlot.Select => "select",
        HotkeySlot.Profile => "profile",
        _ => "capture",
    };

    private static HotkeyDefinition DefaultOf(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => HotkeyDefinition.DefaultInput,
        HotkeySlot.Select => HotkeyDefinition.DefaultSelect,
        HotkeySlot.Profile => HotkeyDefinition.DefaultProfile,
        _ => HotkeyDefinition.DefaultCapture,
    };

    private static IEnumerable<HotkeySlot> OtherSlots(HotkeySlot slot) =>
        Enum.GetValues<HotkeySlot>().Where(other => other != slot);

    private string CurrentHotkeyText(HotkeySlot slot) => slot switch
    {
        HotkeySlot.Input => _settings.HotkeyInputTranslate,
        HotkeySlot.Select => _settings.HotkeySelectTranslate,
        HotkeySlot.Profile => _settings.HotkeySwitchProfile,
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
            default:
                HotkeyCaptureInvalid = invalid;
                break;
        }
    }

    // ==================== FR-014 历史记录 ====================

    public ObservableCollection<TranslationRecord> HistoryItems { get; } = [];

    [ObservableProperty]
    private string _historyKeyword = "";

    [ObservableProperty]
    private string _historyMessage = "";

    partial void OnHistoryKeywordChanged(string value) => RefreshHistory();

    [RelayCommand]
    private void RefreshHistory()
    {
        var records = _history.Search(HistoryKeyword);
        HistoryItems.Clear();
        foreach (var record in records)
        {
            HistoryItems.Add(record);
        }

        HistoryMessage = HistoryItems.Count == 0
            ? (string.IsNullOrWhiteSpace(HistoryKeyword) ? "暂无翻译历史" : "没有匹配的记录")
            : $"共 {HistoryItems.Count} 条" + (HistoryItems.Count >= 500 ? "（仅显示最近 500 条）" : "");
    }

    [RelayCommand]
    private void DeleteHistory(TranslationRecord? record)
    {
        if (record is null)
        {
            return;
        }

        _history.Delete(record.Id);
        RefreshHistory();
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
    private void ClearHistory()
    {
        // FR-014：一键清空需二次确认
        var confirm = MessageBox.Show(
            $"确定要清空全部 {HistoryItems.Count} 条翻译历史吗？此操作不可撤销。",
            "速译 · 清空历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        _history.Clear();
        RefreshHistory();
        HistoryMessage = "已清空翻译历史";
    }

    // ==================== FR-015 生词本 ====================

    public ObservableCollection<VocabularyEntry> VocabularyItems { get; } = [];

    [ObservableProperty]
    private string _vocabularyMessage = "";

    [RelayCommand]
    private void RefreshVocabulary()
    {
        var entries = _vocabulary.List();
        VocabularyItems.Clear();
        foreach (var entry in entries)
        {
            VocabularyItems.Add(entry);
        }

        VocabularyMessage = VocabularyItems.Count == 0
            ? "生词本为空：在翻译小窗点击收藏按钮即可加入"
            : $"共 {VocabularyItems.Count} 个词条";
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

    // ==================== P0 批 1：隐私模式 / 阅读清洗 / 引擎看板 ====================

    /// <summary>隐私模式（spec §2）：本地留痕全关；翻译请求本身仍会发送。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverLockedByPrivacy))]
    private bool _privacyMode;

    /// <summary>阅读清洗（spec §3）：划词/剪贴板文本合并硬换行，默认开。</summary>
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
    }

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

    /// <summary>切到「引擎」页时刷新各卡近 7 天看板。</summary>
    public void RefreshEngineStats()
    {
        foreach (var card in EngineCards)
        {
            card.RefreshStats();
        }
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

    // ==================== 公共 ====================

    internal static AppTheme ToAppTheme(string? value) => value switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };

    private static string NormalizeTheme(string? value) =>
        value is "light" or "dark" ? value : "system";

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
