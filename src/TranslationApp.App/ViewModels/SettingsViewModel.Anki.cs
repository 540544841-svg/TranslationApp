using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TranslationApp.Core.Anki;

namespace TranslationApp.ViewModels;

/// <summary>
/// 设置页「生词本 → Anki 直推」卡片（FR-035 / spec §1.4）：
/// 总开关、牌组/模板/字段名、测试连接、全部直推。全部动作只访问 127.0.0.1:29537，
/// 失败摘要由 Core 客户端脱敏（绝不含用户文本），这里只拼展示文案。
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _ankiEnabled;

    /// <summary>收藏时顺带推送（仅总开关开启时生效）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushAllToAnkiCommand))]
    private bool _ankiPushOnFavorite;

    [ObservableProperty]
    private string _ankiDeck = "生词本";

    [ObservableProperty]
    private string _ankiModel = "基本";

    [ObservableProperty]
    private string _ankiFrontField = "正面";

    [ObservableProperty]
    private string _ankiBackField = "背面";

    /// <summary>测试结果 / 批量进度 / 统计（spec §1.4）。</summary>
    [ObservableProperty]
    private string _ankiStatusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAnkiCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushAllToAnkiCommand))]
    private bool _isAnkiBusy;

    /// <summary>「全部直推」仅总开关开启且不在推送中可用（测试连接不受总开关限制）。</summary>
    private bool CanPushAllToAnki() => AnkiEnabled && !IsAnkiBusy;

    private bool CanTestAnki() => !IsAnkiBusy;

    private void InitializeAnkiPage()
    {
        // 构造期直写 backing field：与主文件各 _xxx = settings.Xxx 同款，避免触发 OnChanged 的重复落盘
#pragma warning disable MVVMTK0034
        _ankiEnabled = _settings.AnkiEnabled;
        _ankiPushOnFavorite = _settings.AnkiPushOnFavorite;
        _ankiDeck = _settings.AnkiDeck;
        _ankiModel = _settings.AnkiModel;
        _ankiFrontField = _settings.AnkiFrontField;
        _ankiBackField = _settings.AnkiBackField;
#pragma warning restore MVVMTK0034
    }

    partial void OnAnkiEnabledChanged(bool value) => Save(s => s.AnkiEnabled = value);

    partial void OnAnkiPushOnFavoriteChanged(bool value) => Save(s => s.AnkiPushOnFavorite = value);

    partial void OnAnkiDeckChanged(string value) => Save(s => s.AnkiDeck = value);

    partial void OnAnkiModelChanged(string value) => Save(s => s.AnkiModel = value);

    partial void OnAnkiFrontFieldChanged(string value) => Save(s => s.AnkiFrontField = value);

    partial void OnAnkiBackFieldChanged(string value) => Save(s => s.AnkiBackField = value);

    [RelayCommand(CanExecute = nameof(CanTestAnki))]
    private async Task TestAnkiAsync()
    {
        IsAnkiBusy = true;
        AnkiStatusText = "正在连接…";
        try
        {
            var probe = await _anki.ProbeAsync();
            AnkiStatusText = probe.Ok
                ? $"AnkiConnect 已连接（版本 {probe.Version}）"
                : $"连接失败：{probe.Reason ?? "未知原因"}——需开着桌面版 Anki 且装有 AnkiConnect 插件";
        }
        finally
        {
            IsAnkiBusy = false;
        }
    }

    /// <summary>
    /// 全部直推（spec §1.3）：整表分批推送，重复项由 Anki 端拒重、计「跳过」；
    /// 本端不存推送状态，重复执行安全（幂等靠 Anki 判重）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPushAllToAnki))]
    private async Task PushAllToAnkiAsync()
    {
        var entries = _vocabulary.List();
        if (entries.Count == 0)
        {
            AnkiStatusText = "生词本为空，没有可推送的词条";
            return;
        }

        IsAnkiBusy = true;
        AnkiStatusText = $"推送中 0 / {entries.Count}…";
        try
        {
            var notes = entries
                .Select(e => AnkiRequestBuilder.FromVocabulary(
                    _settings.AnkiDeck, _settings.AnkiModel,
                    _settings.AnkiFrontField, _settings.AnkiBackField,
                    e.SourceText, e.TranslatedText, e.SourceLanguage, e.TargetLanguage))
                .ToArray();

            var result = await _anki.PushNotesAsync(notes);
            AnkiStatusText = $"新增 {result.Added} · 跳过 {result.Skipped}（重复） · 失败 {result.Failed}"
                + (result.Reason is null ? "" : $"（{result.Reason}）");
        }
        finally
        {
            IsAnkiBusy = false;
        }
    }
}
