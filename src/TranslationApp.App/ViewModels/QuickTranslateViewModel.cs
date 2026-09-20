using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TranslationApp.Core.Anki;
using TranslationApp.Core.ClipboardFormats;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>
/// 翻译小窗 ViewModel（FR-004/005/006/007/014/015/016）：
/// 输入翻译、划词翻译结果、语言交换、结果复制、历史入库、生词收藏、朗读。
/// </summary>
public partial class QuickTranslateViewModel : ObservableObject
{
    public const int MaxInputLength = 3000;

    private readonly TranslatorCatalog _catalog;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly IHistoryRepository _history;
    private readonly IVocabularyRepository _vocabulary;
    private readonly ITtsService _tts;
    private readonly ClipboardMonitor _clipboardMonitor;
    private readonly EngineStatsRepository _stats;
    private readonly AnkiConnectClient? _anki;
    private string? _lastDetectedLanguage;

    /// <summary>P0 批 1：本次会话原文是否被阅读清洗过（成功状态行追加「已清洗换行」）。</summary>
    private bool _cleanedNote;

    /// <summary>P0 批 4 / FR-045：本会话是否点了「重新机器翻译」（点了之后本次输入不再走 TM 回填）。</summary>
    private bool _tmSkipThisSession;

    /// <summary>FR-028：进程内累计降级计数（达阈值时一次性托盘气泡建议改默认引擎）。</summary>
    private readonly EngineFallbackCounter _fallbackCounter = new();

    /// <summary>
    /// FR-028：累计降级达阈值时的托盘气泡回调（由 App 注入，避免 VM 依赖托盘/UI）。
    /// 只做「建议」，绝不代改 <c>settings.Engine</c>（14.4.2）。
    /// </summary>
    public Action<string, string>? NotifyBalloon { get; set; }

    /// <summary>本次会话是否由划词/剪贴板自动进入（决定翻译成功后是否自动朗读原文，FR-016）。</summary>
    private bool _selectionSession;

    public QuickTranslateViewModel(
        TranslatorCatalog catalog,
        AppSettings settings,
        ISettingsStore store,
        IHistoryRepository history,
        IVocabularyRepository vocabulary,
        ITtsService tts,
        ClipboardMonitor clipboardMonitor,
        EngineStatsRepository stats,
        AnkiConnectClient? anki = null)
    {
        _catalog = catalog;
        _settings = settings;
        _store = store;
        _history = history;
        _vocabulary = vocabulary;
        _tts = tts;
        _clipboardMonitor = clipboardMonitor;
        _stats = stats;
        _anki = anki;
        _targetLanguage = settings.TargetLanguage;
    }

    public IReadOnlyList<TranslationLanguages.LanguageOption> SourceOptions => TranslationLanguages.SourceOptions;

    public IReadOnlyList<TranslationLanguages.LanguageOption> TargetOptions => TranslationLanguages.TargetOptions;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(IsResultPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(IsSingleResultVisible))]
    [NotifyCanExecuteChangedFor(nameof(SpeakResultCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFavoriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyResultCommand))]
    private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultPlaceholderVisible))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>术语表命中徽标文案（P0 批 1）：空 = 本次译文无术语替换。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlossaryNote))]
    private string _glossaryNote = "";

    /// <summary>是否显示「术语 ×N」徽标。</summary>
    public bool HasGlossaryNote => GlossaryNote.Length > 0;

    /// <summary>命中明细（「源 → 目标 ×次数」逐行），供徽标 tooltip。</summary>
    [ObservableProperty]
    private string _glossaryTooltip = "";

    /// <summary>标记本次会话原文经过阅读清洗（呼出方在 ShowForSelection 里调用）。</summary>
    internal void MarkCleaned() => _cleanedNote = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFavoriteActive))]
    private bool _isFavorited;

    [ObservableProperty]
    private string _sourceLanguage = TranslationLanguages.AutoCode;

    [ObservableProperty]
    private string _targetLanguage;

    /// <summary>是否有译文（控制译文区与「复制译文」按钮的显隐）。</summary>
    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    /// <summary>是否显示错误提示条。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>是否显示普通状态/提示文字。</summary>
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    /// <summary>空状态占位提示：既无译文也不在翻译中，也不在对比模式。</summary>
    public bool IsResultPlaceholderVisible => !HasResult && !IsBusy && !IsComparisonVisible && !IsCompareHintVisible;

    /// <summary>收藏按钮的激活态（已收藏为实心/主色）。</summary>
    public bool IsFavoriteActive => IsFavorited;

    /// <summary>系统是否安装了可朗读原文的语音包（无则按钮禁用）。</summary>
    public bool CanSpeak => _tts.InstalledVoiceCount > 0;

    /// <summary>朗读按钮的提示文案：无语音包时说明原因。</summary>
    public string SpeakToolTip => CanSpeak
        ? "朗读"
        : "系统未安装语音包，请在「设置 → 时间和语言 → 语音」中添加";

    /// <summary>截图翻译按钮提示（带当前热键，FR-021）。</summary>
    public string CaptureToolTip => $"截图翻译 ({_settings.HotkeyCaptureTranslate})";

    partial void OnInputTextChanged(string value)
    {
        StatusText = value.Length >= MaxInputLength ? $"已达 {MaxInputLength} 字符上限" : "";
        SpeakSourceCommand.NotifyCanExecuteChanged();
        ToggleCompareCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetLanguageChanged(string value)
    {
        // FR-007：目标语言切换立即生效并记忆（设置页与小窗共用同一设置对象）
        if (!string.IsNullOrEmpty(value) && _settings.TargetLanguage != value)
        {
            _settings.TargetLanguage = value;
            _store.Save(_settings);
        }

        // 13.4.1：语言变更后已有对比结果不再对应当前语言对，清空并提示需重新对比
        ClearCompareResultsForLanguageChange();
    }

    partial void OnSourceLanguageChanged(string value) => ClearCompareResultsForLanguageChange();

    /// <summary>
    /// 每次呼出重置会话状态（notice 用于取词失败等提示，initialText 用于划词带入原文）。
    /// sourceLanguage 供 OCR 截图翻译按识别语言直接指定源语言（13.2.5 规则 2），为 null 时保持自动检测。
    /// </summary>
    public void ResetForShow(string? notice, string targetLanguage, string? initialText = null, string? sourceLanguage = null)
    {
        InputText = initialText ?? "";
        ResultText = "";
        ErrorText = "";
        StatusText = notice ?? "";
        SourceLanguage = string.IsNullOrEmpty(sourceLanguage) ? TranslationLanguages.AutoCode : sourceLanguage;
        TargetLanguage = targetLanguage;
        IsFavorited = false;
        _lastDetectedLanguage = null;
        _cleanedNote = false;
        GlossaryNote = "";
        GlossaryTooltip = "";
        _selectionSession = false; // 会话重置即清除，由 SetSelectionSession 在呼出时重新标记
        _tts.Stop(); // 呼出新会话时停止上一次朗读

        // P0 批 4：TM 跳过标记与对照视图状态随会话重置
        _tmSkipThisSession = false;
        IsTmHit = false;
        IsAlignView = false;
        AlignPairs.Clear();

        // FR-020 AC 4：再次打开小窗不残留上次对比结果，并取消可能仍在途的请求
        CancelCompareRequests();
        CompareItems.Clear();
        _compareSignature = "";
        IsComparing = false;
        NotifyCompareLayoutChanged();
    }

    /// <summary>
    /// 标记本次会话的来源（由 QuickWindow 呼出时调用，FR-016）：划词会话才可能在翻译成功后自动朗读原文。
    /// </summary>
    internal void SetSelectionSession(bool fromSelection) => _selectionSession = fromSelection;

    [RelayCommand]
    private async Task TranslateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var text = InputText?.Trim() ?? "";
        Log.Debug("翻译请求：输入 {Length} 字符，{Source}→{Target}", text.Length, SourceLanguage, TargetLanguage);
        if (text.Length == 0)
        {
            ErrorText = "请输入要翻译的文本";
            return;
        }

        var translator = _catalog.Resolve(_settings.Engine);

        // FR-045（P0 批 4）：TM 相似句回填——本地查询替代网络请求，只会更快，不破落定延迟红线
        if (_settings.TmReuseEnabled && !_tmSkipThisSession)
        {
            var tmHit = TmMatcher.Find(text, _history.TmCandidates(TargetLanguage));
            if (tmHit is not null)
            {
                ApplyTmSuccess(text, tmHit);
                return;
            }
        }

        IsBusy = true;
        ErrorText = "";
        ResultText = "";
        StatusText = "翻译中…";

        try
        {
            var result = await translator.TranslateAsync(text, SourceLanguage, TargetLanguage);
            ApplySuccess(translator, text, result);
            StatusText = _cleanedNote ? "已清洗换行" : "";
        }
        catch (TranslationException ex)
        {
            if (TryResolveFallback(translator, ex, out var fallback))
            {
                await TranslateWithFallbackAsync(text, translator, fallback, ex);
            }
            else
            {
                ReportFailure(translator, ex);
            }
        }
        catch (Exception ex)
        {
            ErrorText = "发生未知错误";
            StatusText = "";
            Log.Error(ex, "翻译出现未预期异常（引擎={Engine}）", translator.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>成功翻译的统一收尾：回填译文/检测语言、入库（FR-014）、刷新收藏（FR-015）、划词自动朗读（FR-016）。</summary>
    private void ApplySuccess(ITranslator translator, string text, TranslationResult result)
    {
        ResultText = result.TranslatedText;
        if (!string.IsNullOrEmpty(result.DetectedSourceLanguage))
        {
            _lastDetectedLanguage = result.DetectedSourceLanguage;
            if (SourceLanguage == TranslationLanguages.AutoCode)
            {
                // FR-007：以翻译结果回填检测到的源语言
                SourceLanguage = result.DetectedSourceLanguage;
            }
        }

        // 只记元数据，不记录用户文本内容（需求 6 安全：日志脱敏）
        Log.Information(
            "翻译成功（引擎={Engine}，{Source}→{Target}，原文 {InputLength} 字符，译文 {OutputLength} 字符）",
            translator.Name, SourceLanguage, TargetLanguage, text.Length, result.TranslatedText.Length);

        // FR-014：成功翻译自动入库（引擎名写**实际使用的引擎**，降级成功即记为 Bing）；FR-015：刷新收藏态
        // P0 批 1 / spec §2.2：隐私模式下不写历史（生词本收藏是用户主动动作，不受影响）
        if (!_settings.PrivacyMode)
        {
            _history.Add(text, result.TranslatedText, SourceLanguage, TargetLanguage, translator.Name);
        }
        RefreshFavoriteState();

        // P0 批 1：术语表命中徽标（命中数与明细由 GlossaryTranslator 回填）
        // 批 3 / FR-041：反向保护跳过的冲突词条并入徽标与 tooltip
        var conflicts = result.GlossaryConflicts;
        GlossaryNote = result.GlossaryHits > 0
            ? conflicts is { Count: > 0 }
                ? $"术语 ×{result.GlossaryHits} · 冲突跳过 ×{conflicts.Count}"
                : $"术语 ×{result.GlossaryHits}"
            : conflicts is { Count: > 0 }
                ? $"术语冲突跳过 ×{conflicts.Count}"
                : "";
        var tooltipLines = new List<string>();
        if (result.GlossaryApplied is { Count: > 0 } applied)
        {
            tooltipLines.AddRange(applied.Select(a => $"{a.Source} → {a.Target} ×{a.Count}"));
        }
        if (conflicts is { Count: > 0 })
        {
            tooltipLines.AddRange(conflicts.Select(c => $"跳过 {c.Source} → {c.Target}（原文已含该译法）"));
        }
        GlossaryTooltip = string.Join("\n", tooltipLines);

        // FR-043（P0 批 4）：≥3 段且段数对齐时提供「对照」视图（默认仍是整块译文）
        UpdateAlignment(text, result.TranslatedText);

        // FR-016：划词会话的首次翻译成功后自动朗读原文（手动输入翻译不朗读）
        TryAutoSpeakSource(text);
    }

    /// <summary>失败上报：错误条按分类给出文案，状态行必须清掉「翻译中…」（否则与错误提示自相矛盾）。</summary>
    private void ReportFailure(ITranslator translator, TranslationException ex)
    {
        ErrorText = DescribeError(ex.ErrorType);
        StatusText = "";
        Log.Warning("翻译失败（引擎={Engine}，错误类型={ErrorType}）：{Reason}",
            translator.Name, ex.ErrorType, ex.Message);
    }

    /// <summary>
    /// FR-028：判定并解析备用引擎。判定矩阵在 Core 的 <see cref="EngineFallback"/>（可穷举单测），
    /// 这里只负责**精确**解析引擎实例——用 <see cref="TranslatorCatalog.Find"/> 而非 Resolve，
    /// 这样「备用引擎未配置 / 不存在」不会被静默换成另一个引擎的配额。
    /// </summary>
    private bool TryResolveFallback(
        ITranslator primary, TranslationException error, [NotNullWhen(true)] out ITranslator? fallback)
    {
        var candidate = _catalog.Find(_settings.FallbackEngineId);
        var should = EngineFallback.ShouldFallback(
            error.ErrorType,
            _settings.EnableEngineFallback,
            primary.Id,
            candidate?.Id,
            candidate?.IsConfigured == true);

        fallback = should ? candidate : null;
        return fallback is not null;
    }

    /// <summary>
    /// FR-028：主引擎失败后**只降一级**降到备用引擎（固定 <c>FallbackEngineId</c>，不做链式降级）。
    /// 降级必须让用户知情：等待期间状态行先给出「正在改用…」，成功后保留降级事实；
    /// 两引擎都失败时**只保留一条**错误条（主引擎的分类文案 = 主因），状态行补充说明已试过备用引擎。
    /// </summary>
    private async Task TranslateWithFallbackAsync(
        string text, ITranslator primary, ITranslator fallback, TranslationException primaryError)
    {
        StatusText = EngineFallback.InProgressStatus(primary.Name, fallback.Name);
        Log.Information("引擎降级：{Primary} → {Fallback}，原因={ErrorType}",
            primary.Id, fallback.Id, primaryError.ErrorType);

        try
        {
            var result = await fallback.TranslateAsync(text, SourceLanguage, TargetLanguage);
            ApplySuccess(fallback, text, result);
            StatusText = EngineFallback.SuccessStatus(primary.Name, fallback.Name, primaryError.Message)
                + (_cleanedNote ? " · 已清洗换行" : "");
            // P0 批 1 / spec §4.2：主引擎「失败后被降级救回」计 FallbackUsed（失败列已由装饰器记过，不重复）
            if (!_settings.PrivacyMode)
            {
                _stats.Record(primary.Id, EngineOutcome.FallbackUsed);
            }
            Log.Information("引擎降级成功：{Fallback}（原文 {InputLength} 字符，译文 {OutputLength} 字符）",
                fallback.Id, text.Length, result.TranslatedText.Length);
            NotifyFallbackThreshold(primary, fallback);
        }
        catch (TranslationException fallbackError)
        {
            ErrorText = DescribeError(primaryError.ErrorType);
            StatusText = EngineFallback.BothFailedStatus(fallback.Name);
            Log.Warning("降级后仍失败（备用={Fallback}，错误类型={ErrorType}）：{Reason}",
                fallback.Id, fallbackError.ErrorType, fallbackError.Message);
        }
    }

    /// <summary>
    /// 累计降级达阈值时弹**一次性**托盘气泡建议改默认引擎（14.4.2）。
    /// 只在降级**成功**后计数：两个引擎都不可用时建议「改用 Bing」是不负责任的。
    /// </summary>
    private void NotifyFallbackThreshold(ITranslator primary, ITranslator fallback)
    {
        if (!_fallbackCounter.Record())
        {
            return;
        }

        Log.Information("进程内累计降级 {Count} 次，已用托盘气泡建议改默认引擎", _fallbackCounter.Count);
        NotifyBalloon?.Invoke("速译", EngineFallback.SuggestionBalloon(primary.Name, fallback.Name));
    }

    /// <summary>FR-006：错误分类给出不同提示文案。</summary>
    internal static string DescribeError(TranslationErrorType errorType) => errorType switch
    {
        TranslationErrorType.Network => "网络不可达或请求超时，请检查网络后重试",
        TranslationErrorType.InvalidKey => "API Key 无效或未配置，请在设置中检查",
        TranslationErrorType.QuotaExceeded => "引擎配额已用尽，请更换引擎或稍后重试",
        _ => "翻译引擎异常，请稍后重试",
    };

    [RelayCommand]
    private void Swap()
    {
        // Ctrl+Enter 交换源/目标语言（FR-004）；源为自动检测时用上次检测结果兜底
        var oldSource = SourceLanguage;
        var oldTarget = TargetLanguage;
        SourceLanguage = oldTarget;
        TargetLanguage = oldSource == TranslationLanguages.AutoCode
            ? (_lastDetectedLanguage ?? "en")
            : oldSource;
    }

    private bool HasResultText() => HasResult;

    [RelayCommand(CanExecute = nameof(HasResultText))]
    private void CopyResult()
    {
        try
        {
            // 抑制剪贴板监听：这是本程序写剪贴板，不应被当作「用户复制」而再次触发翻译（FR-017）
            _clipboardMonitor.Suppress(TimeSpan.FromSeconds(1));
            System.Windows.Clipboard.SetText(ResultText);
            StatusText = "译文已复制";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制译文到剪贴板失败");
        }
    }

    // ==================== P0 批 4：TM 回填 / 段落对照 / 复制策略 ====================

    /// <summary>
    /// TM 命中的收尾（FR-045 / spec §3）：直接回填历史译文并标注来源（A2-2 可视化），
    /// 不重复入库（同句同译早已在历史里）、不记引擎统计（没有引擎调用）；
    /// 「重新机器翻译」给一次点击机会，点了本会话强制走引擎。
    /// </summary>
    private void ApplyTmSuccess(string text, TmHit hit)
    {
        ResultText = hit.Entry.Translated;
        IsTmHit = true;
        var age = DateTimeOffset.UtcNow - hit.Entry.CreatedAt;
        var ageText = age.TotalDays < 1 ? "今天的记录" : $"{age.TotalDays:0} 天前的记录";
        StatusText = $"TM 命中 {hit.Score:P0} · 来自 {ageText}";
        UpdateAlignment(text, hit.Entry.Translated);
        RefreshFavoriteState();
        TryAutoSpeakSource(text);
    }

    /// <summary>FR-045：TM 回填后强制用引擎重译本句（会话级跳过，切句/重开小窗后恢复）。</summary>
    [RelayCommand(CanExecute = nameof(CanReTranslateMachine))]
    private async Task ReTranslateMachineAsync()
    {
        _tmSkipThisSession = true;
        IsTmHit = false;
        await TranslateAsync();
    }

    private bool CanReTranslateMachine() => IsTmHit;

    /// <summary>一段原文 + 对应译文（FR-043 对照视图行）。</summary>
    public sealed record ParagraphPairView(string Source, string Translated);

    /// <summary>逐段对照对（对齐失败为空）。</summary>
    public ObservableCollection<ParagraphPairView> AlignPairs { get; } = [];

    /// <summary>本句是否可对照（≥3 段且段数对齐）——决定「对照」按钮是否出现。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleAlignViewCommand))]
    private bool _hasAlignment;

    /// <summary>当前是否显示对照视图（默认关，整块译文）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlockViewVisible))]
    private bool _isAlignView;

    /// <summary>整块译文区是否显示（对照打开时隐藏）。</summary>
    public bool IsBlockViewVisible => !IsAlignView;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReTranslateMachineCommand))]
    private bool _isTmHit;

    private void UpdateAlignment(string source, string translated)
    {
        AlignPairs.Clear();
        var pairs = ParagraphAligner.Align(source, translated);
        HasAlignment = pairs is not null;
        if (pairs is null)
        {
            return;
        }

        foreach (var (src, tgt) in pairs)
        {
            AlignPairs.Add(new ParagraphPairView(src, tgt));
        }
    }

    [RelayCommand(CanExecute = nameof(HasAlignment))]
    private void ToggleAlignView() => IsAlignView = !IsAlignView;

    /// <summary>FR-044：只复制原文。</summary>
    [RelayCommand(CanExecute = nameof(HasResultText))]
    private void CopySource() => CopyToClipboard(
        ClipboardContentBuilder.Build(InputText ?? "", ResultText, ClipboardCopyMode.SourceOnly), "原文已复制");

    /// <summary>FR-044：复制原文+译文（空行分隔，listing 场景粘贴后可整块删改）。</summary>
    [RelayCommand(CanExecute = nameof(HasResultText))]
    private void CopyBoth() => CopyToClipboard(
        ClipboardContentBuilder.Build(InputText ?? "", ResultText, ClipboardCopyMode.Both), "原文+译文已复制");

    private void CopyToClipboard(string content, string doneText)
    {
        try
        {
            // 与复制译文同一条纪律：自写抑制，避免触发剪贴板监听自我循环（FR-017）
            _clipboardMonitor.Suppress(TimeSpan.FromSeconds(1));
            System.Windows.Clipboard.SetText(content);
            StatusText = doneText;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制到剪贴板失败");
        }
    }

    private bool CanSpeakSource() => CanSpeak && !string.IsNullOrWhiteSpace(InputText);

    /// <summary>朗读原文（FR-016）。</summary>
    [RelayCommand(CanExecute = nameof(CanSpeakSource))]
    private void SpeakSource() => Speak(InputText, ResolveSourceLanguage());

    private bool CanSpeakResult() => CanSpeak && HasResult;

    /// <summary>朗读译文（FR-016）。</summary>
    [RelayCommand(CanExecute = nameof(CanSpeakResult))]
    private void SpeakResult() => Speak(ResultText, TargetLanguage);

    private void Speak(string? text, string languageCode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            if (_tts.IsSpeaking)
            {
                _tts.Stop();
                Log.Debug("朗读：已停止");
                return; // 播放中再次点击 = 停止
            }

            _tts.Speak(text, languageCode);
            // 只记语言与字符数，不记录被朗读的内容
            Log.Debug("朗读开始：语言={Language}，{Length} 字符", languageCode, text.Length);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "朗读失败");
        }
    }

    /// <summary>源语言为自动检测时，用检测结果或英语兜底（决定用哪个语音包）。</summary>
    private string ResolveSourceLanguage() =>
        SourceLanguage == TranslationLanguages.AutoCode
            ? _lastDetectedLanguage ?? "en"
            : SourceLanguage;

    /// <summary>
    /// FR-016「划词后自动朗读原文」：仅当本次会话来自划词、设置已开启、系统装有对应语音包时朗读一次。
    /// 手动输入翻译不触发；用户手动点朗读按钮的行为不受影响（会话内一次性，不重复打断）。
    /// </summary>
    private void TryAutoSpeakSource(string text)
    {
        if (!_selectionSession)
        {
            return;
        }

        _selectionSession = false; // 一次性：同一会话内用户再手动翻译不重复自动朗读

        // 用户已在朗读（手动点了朗读按钮）时不打断，尊重用户操作
        if (!_settings.AutoSpeakAfterSelect || _tts.IsSpeaking || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var language = ResolveSourceLanguage(); // 自动检测时已在成功分支回填检测结果
        if (!_tts.IsLanguageSupported(language))
        {
            Log.Debug("划词自动朗读跳过：语言 {Language} 无可用语音包", language);
            return;
        }

        Speak(text, language);
    }

    private bool CanToggleFavorite() => HasResult;

    /// <summary>收藏 / 取消收藏到生词本（FR-015）。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleFavorite))]
    private void ToggleFavorite()
    {
        var source = InputText?.Trim() ?? "";
        if (string.IsNullOrEmpty(source) || !HasResult)
        {
            return;
        }

        if (IsFavorited)
        {
            var existing = _vocabulary.List()
                .FirstOrDefault(v => v.SourceText == source && v.TargetLanguage == TargetLanguage);
            if (existing is not null)
            {
                _vocabulary.Delete(existing.Id);
            }

            IsFavorited = false;
            StatusText = "已从生词本移除";
            return;
        }

        _vocabulary.Add(source, ResultText, SourceLanguage, TargetLanguage);
        IsFavorited = true;
        StatusText = "已加入生词本";
        TryPushToAnki(source, ResultText);
    }

    /// <summary>
    /// FR-035：Anki 推送完成回调（由 App 注入，负责调度回 UI 线程再调 <see cref="AppendAnkiResult"/>）。
    /// 与 <see cref="NotifyBalloon"/> 同款解耦：VM 不依赖 Application/托盘。
    /// </summary>
    public Action<bool, string?>? NotifyAnkiPushed { get; set; }

    /// <summary>
    /// 收藏成功后顺带推 Anki（spec §1.3）：fire-and-forget，推送快慢不影响收藏与状态行；
    /// 失败只降级为状态行追加一句，用户仍可在设置页批量补推。隐私模式不拦截——收藏是显式动作。
    /// </summary>
    private void TryPushToAnki(string source, string translated)
    {
        if (_anki is null || !_settings.AnkiEnabled || !_settings.AnkiPushOnFavorite)
        {
            return;
        }

        var sourceLanguage = SourceLanguage == TranslationLanguages.AutoCode
            ? _lastDetectedLanguage ?? TranslationLanguages.AutoCode
            : SourceLanguage;
        var note = AnkiRequestBuilder.FromVocabulary(
            _settings.AnkiDeck, _settings.AnkiModel, _settings.AnkiFrontField, _settings.AnkiBackField,
            source, translated, sourceLanguage, TargetLanguage);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _anki.PushNotesAsync([note]);
                NotifyAnkiPushed?.Invoke(result.Added > 0, result.Reason);
            }
            catch (Exception ex)
            {
                // 理论上 PushNotesAsync 内部已全捕获；这里是最后一道防线，绝不让推送弄崩小窗
                Log.Debug(ex, "Anki 收藏直推异常（忽略）");
            }
        });
    }

    /// <summary>Anki 推送结果回填状态行；状态行已被更新的操作改写时不抢话。</summary>
    public void AppendAnkiResult(bool added, string? reason)
    {
        if (!StatusText.StartsWith("已加入生词本", StringComparison.Ordinal))
        {
            return;
        }

        StatusText = added
            ? "已加入生词本 · 已推送 Anki"
            : $"已加入生词本 · Anki 未推送（{reason ?? "连接失败"}）";
    }

    /// <summary>刷新当前「原文 + 目标语言」是否已在生词本中。</summary>
    private void RefreshFavoriteState()
    {
        var source = InputText?.Trim() ?? "";
        IsFavorited = !string.IsNullOrEmpty(source)
                      && _vocabulary.Contains(source, TargetLanguage);
    }

    // ==================== FR-020 引擎结果对比（13.4）====================

    /// <summary>对比结果栏（2 栏横排 / ≥3 栏纵排，13.4.2）。</summary>
    public ObservableCollection<EngineResultViewModel> CompareItems { get; } = [];

    private CancellationTokenSource? _compareCts;
    private string _compareSignature = "";

    /// <summary>是否处于对比模式。</summary>
    [ObservableProperty]
    private bool _isComparing;

    /// <summary>对比栏数（布局由它自动决定，用户无需手选）。</summary>
    public int ComparisonColumnCount => CompareItems.Count;

    /// <summary>2 个引擎 → 横向分栏。</summary>
    public bool IsComparisonHorizontal => IsComparing && CompareItems.Count == 2;

    /// <summary>3 个及以上 → 纵向堆叠。</summary>
    public bool IsComparisonVertical => IsComparing && CompareItems.Count >= 3;

    public bool IsComparisonVisible => IsComparisonHorizontal || IsComparisonVertical;

    /// <summary>对比结果被清空（语言/引擎集合变更）后显示「请重新对比」提示。</summary>
    public bool IsCompareHintVisible => IsComparing && CompareItems.Count == 0;

    /// <summary>单栏译文视图（对比模式下隐藏，由分栏结果区接管）。</summary>
    public bool IsSingleResultVisible => HasResult && !IsComparing;

    /// <summary>是否有对比栏在途。</summary>
    public bool IsCompareBusy => CompareItems.Any(item => item.IsBusy);

    /// <summary>窗口顶部进度条：单栏翻译中或任一对比栏在途（13.4.2）。</summary>
    public bool IsProgressVisible => IsBusy || IsCompareBusy;

    /// <summary>
    /// 「对比」按钮可用性（13.4.1）：对比模式下始终可点（用于退出）；
    /// 否则要求已配置的对比引擎达到 2 个。
    /// </summary>
    public bool CanCompare => IsComparing || ResolveCompareEngines().Count >= EngineComparison.MinEngines;

    public string CompareToolTip => IsComparing
        ? "退出对比"
        : CanCompare
            ? "对比多个引擎"
            : $"请先在「设置 → 翻译 → 结果对比」中勾选 {EngineComparison.MinEngines} 个已配置引擎";

    partial void OnIsComparingChanged(bool value)
    {
        OnPropertyChanged(nameof(CompareToolTip));
        NotifyCompareLayoutChanged();
    }

    /// <summary>
    /// 点击「对比」进入/退出对比模式（13.4.1）。
    /// AllowConcurrentExecutions：对比可能持续数十秒，期间按钮必须保持可用，
    /// 否则用户无法「再次点击退出对比」（退出即取消在途请求，13.4.3）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCompare), AllowConcurrentExecutions = true)]
    private async Task ToggleCompareAsync()
    {
        if (IsComparing)
        {
            ExitCompare();
            return;
        }

        var text = InputText?.Trim() ?? "";
        if (text.Length == 0)
        {
            ErrorText = "请输入要翻译的文本";
            return;
        }

        var engines = ResolveCompareEngines();
        if (engines.Count < EngineComparison.MinEngines)
        {
            ErrorText = $"对比需要至少 {EngineComparison.MinEngines} 个已配置引擎，请在「设置 → 翻译 → 结果对比」中勾选";
            return;
        }

        ErrorText = "";
        StatusText = "";
        _tts.Stop(); // 对比结果分栏展示，先停掉上一次朗读

        CompareItems.Clear();
        var vertical = engines.Count > EngineComparison.MinEngines; // 2 栏横排，≥3 栏纵排
        var items = engines.Select(engine => new EngineResultViewModel(this, engine, vertical)).ToArray();
        foreach (var item in items)
        {
            CompareItems.Add(item);
        }

        _compareSignature = EngineComparison.Signature(engines);
        IsComparing = true; // 放在填充之后：代码后置按栏数决定临时窗口高度（13.4.2）

        CancelCompareRequests();
        var token = CompareToken();
        Log.Information("开始引擎对比：{Engines}，原文 {Length} 字符", _compareSignature, text.Length);

        await EngineComparison.RunAsync(
            engines, text, SourceLanguage, TargetLanguage,
            (index, outcome) => OnCompareItemCompleted(items[index], outcome), token);
    }

    /// <summary>重试单栏（13.4.3）：只重发该栏，其他栏不受影响。</summary>
    internal async Task RetryCompareItemAsync(EngineResultViewModel item)
    {
        var index = CompareItems.IndexOf(item);
        var text = InputText?.Trim() ?? "";
        if (item.IsBusy || index < 0 || text.Length == 0)
        {
            return;
        }

        item.Text = "";
        item.ErrorText = "";
        item.IsBusy = true;
        Log.Information("重试对比栏：{Engine}", item.EngineName);

        await EngineComparison.RunAsync(
            [item.Engine], text, SourceLanguage, TargetLanguage,
            (_, outcome) => OnCompareItemCompleted(item, outcome),
            CompareToken());
    }

    /// <summary>每栏的复制（同样抑制剪贴板监听，避免被当作「用户复制」再触发翻译）。</summary>
    internal void CopyCompareText(string text)
    {
        try
        {
            _clipboardMonitor.Suppress(TimeSpan.FromSeconds(1));
            System.Windows.Clipboard.SetText(text);
            StatusText = "译文已复制";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制对比译文失败");
        }
    }

    /// <summary>每栏的朗读（FR-016）。</summary>
    internal void SpeakCompareText(string text) => Speak(text, TargetLanguage);

    /// <summary>栏内在途状态变化 → 顶部进度条随之显隐。</summary>
    internal void OnCompareItemBusyChanged()
    {
        OnPropertyChanged(nameof(IsCompareBusy));
        OnPropertyChanged(nameof(IsProgressVisible));
    }

    /// <summary>关窗 / Esc / 退出对比：取消所有在途请求（13.4.3）。</summary>
    public void CancelComparison()
    {
        CancelCompareRequests();
        foreach (var item in CompareItems)
        {
            item.IsBusy = false;
        }
    }

    /// <summary>
    /// 小窗重新激活时检测「对比引擎勾选集合」是否变更（13.4.1）：变了则清空结果并提示重新对比。
    /// </summary>
    internal bool NotifyCompareSelectionChanged()
    {
        if (!IsComparing || EngineComparison.Signature(ResolveCompareEngines()) == _compareSignature)
        {
            return false;
        }

        ClearCompareResultsForLanguageChange();
        StatusText = "对比引擎已变更，请重新点击「对比」";
        return true;
    }

    private void OnCompareItemCompleted(EngineResultViewModel item, EngineComparisonOutcome outcome)
    {
        // 按引用校验目标栏：上一轮对比（已退出/已清空）的迟到回调不得写进新一轮的栏
        if (!CompareItems.Contains(item))
        {
            return;
        }
        item.IsBusy = false;

        if (outcome.IsCancelled)
        {
            // 取消不是失败：清掉半成品即可，不显示错误（13.4.3）
            item.Text = "";
            item.ErrorText = "";
            return;
        }

        if (!outcome.HasText)
        {
            var errorType = outcome.ErrorType ?? TranslationErrorType.Engine;
            item.ErrorText = DescribeError(errorType);
            Log.Warning("对比失败（引擎={Engine}，错误类型={ErrorType}）", outcome.EngineName, errorType);
            return;
        }

        item.ErrorText = "";
        item.Text = outcome.Text!;
        // 只记元数据，不记录用户文本内容（需求 6 安全：日志脱敏）
        Log.Information(
            "对比完成（引擎={Engine}，原文 {InputLength} 字符，译文 {OutputLength} 字符）",
            outcome.EngineName, InputText?.Trim().Length ?? 0, outcome.Text!.Length);

        if (_settings.CompareIncludeInHistory)
        {
            // 13.4.1：对比模式下每个引擎的译文各存一条，引擎字段可区分
            if (!_settings.PrivacyMode)
            {
                _history.Add(InputText?.Trim() ?? "", outcome.Text!, SourceLanguage, TargetLanguage, outcome.EngineName);
            }
        }
    }

    /// <summary>语言或对比引擎集合变更：清空结果并提示需重新对比（13.4.1）。</summary>
    private void ClearCompareResults()
    {
        if (!IsComparing)
        {
            return;
        }

        CancelCompareRequests();
        CompareItems.Clear();
        _compareSignature = "";
        NotifyCompareLayoutChanged();
    }

    /// <summary>语言变更：清空结果并提示（13.4.1）。</summary>
    private void ClearCompareResultsForLanguageChange()
    {
        if (!IsComparing)
        {
            return;
        }

        ClearCompareResults();
        StatusText = "语言已变更，请重新点击「对比」";
    }

    private void ExitCompare()
    {
        var currentEngineId = _catalog.Resolve(_settings.Engine).Id;
        var reusable = CompareItems.FirstOrDefault(item => item.Engine.Id == currentEngineId && item.HasText);

        CancelCompareRequests();
        foreach (var item in CompareItems)
        {
            item.IsBusy = false;
        }

        CompareItems.Clear();
        IsComparing = false;
        _compareSignature = "";

        if (reusable is not null)
        {
            // 13.4.1：退出对比时若本次已有当前引擎的译文则直接复用，不重复请求
            ResultText = reusable.Text;
            RefreshFavoriteState();
        }

        Log.Debug("退出对比模式");
    }

    private IReadOnlyList<ITranslator> ResolveCompareEngines() =>
        EngineComparison.ResolveEngines(_settings.CompareEngineIds, _catalog.All, _settings.Engine);

    private CancellationToken CompareToken()
    {
        _compareCts ??= new CancellationTokenSource();
        return _compareCts.Token;
    }

    /// <summary>
    /// 取消在途对比请求。刻意不 Dispose 令牌源：请求内部会 Dispose 自己的链接令牌，
    /// 而此处立即 Dispose 会让尚未进入请求阶段的引擎抛 ObjectDisposedException（被误判成引擎异常）。
    /// </summary>
    private void CancelCompareRequests()
    {
        var cts = _compareCts;
        _compareCts = null;
        cts?.Cancel();
    }

    private void NotifyCompareLayoutChanged()
    {
        OnPropertyChanged(nameof(ComparisonColumnCount));
        OnPropertyChanged(nameof(IsComparisonHorizontal));
        OnPropertyChanged(nameof(IsComparisonVertical));
        OnPropertyChanged(nameof(IsComparisonVisible));
        OnPropertyChanged(nameof(IsCompareHintVisible));
        OnPropertyChanged(nameof(IsSingleResultVisible));
        OnPropertyChanged(nameof(IsResultPlaceholderVisible));
        OnPropertyChanged(nameof(IsCompareBusy));
        OnPropertyChanged(nameof(IsProgressVisible));
        ToggleCompareCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// 对比模式下的一栏（13.4.4）：引擎名 + 译文 / 在途 / 错误三态 + 每栏独立的复制、朗读与重试。
/// 属性由 <see cref="EngineComparison.RunAsync"/> 的回调在 UI 线程更新（先到先显示）。
/// </summary>
public sealed partial class EngineResultViewModel : ObservableObject
{
    private readonly QuickTranslateViewModel _owner;

    internal EngineResultViewModel(QuickTranslateViewModel owner, ITranslator engine, bool verticalLayout)
    {
        _owner = owner;
        Engine = engine;
        IsVerticalLayout = verticalLayout;
        _isBusy = true;
    }

    internal ITranslator Engine { get; }

    /// <summary>栏头显示的引擎名（TextTrimming 由视图负责）。</summary>
    public string EngineName => Engine.Name;

    /// <summary>纵排时在栏尾画分隔线（横排改用间距，见 13.4.4）。</summary>
    public bool IsVerticalLayout { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SpeakTextCommand))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    public bool HasText => !string.IsNullOrEmpty(Text);

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>系统是否安装了语音包（无则朗读按钮禁用）。</summary>
    public bool CanSpeak => _owner.CanSpeak;

    public string SpeakToolTip => _owner.SpeakToolTip;

    partial void OnIsBusyChanged(bool value) => _owner.OnCompareItemBusyChanged();

    private bool CanUseText() => HasText;

    [RelayCommand(CanExecute = nameof(CanUseText))]
    private void Copy() => _owner.CopyCompareText(Text);

    [RelayCommand(CanExecute = nameof(CanUseText))]
    private void SpeakText() => _owner.SpeakCompareText(Text);

    [RelayCommand]
    private Task Retry() => _owner.RetryCompareItemAsync(this);
}
