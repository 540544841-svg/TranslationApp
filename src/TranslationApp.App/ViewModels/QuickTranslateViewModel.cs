using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TranslationApp.Core.Anki;
using TranslationApp.Core.ClipboardFormats;
using TranslationApp.Core.Dictionary;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Core.Vocabulary;

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

    /// <summary>FR-049：mdx 离线词典（可为 null = 未启用词典层）。只读本地文件，不产生网络请求。</summary>
    private readonly DictionaryManager? _dictionaries;
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

    /// <summary>本次会话的前台应用进程名（FR-058；空 = 不知道 / 不适用）。</summary>
    private string _foregroundApp = "";

    public QuickTranslateViewModel(
        TranslatorCatalog catalog,
        AppSettings settings,
        ISettingsStore store,
        IHistoryRepository history,
        IVocabularyRepository vocabulary,
        ITtsService tts,
        ClipboardMonitor clipboardMonitor,
        EngineStatsRepository stats,
        AnkiConnectClient? anki = null,
        DictionaryManager? dictionaries = null)
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
        _dictionaries = dictionaries;
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
    [NotifyCanExecuteChangedFor(nameof(ToggleShadowCommand))]
    private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResultPlaceholderVisible))]
    [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
    [NotifyCanExecuteChangedFor(nameof(ApplyStyleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleShadowCommand))]
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

    /// <summary>FR-049：词典释义（空 = 本次没有词典命中；不在 UI 线程外的回调里赋值）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDictionaryVisible))]
    private string _dictionaryDefinition = "";

    /// <summary>词典命中的词头（与输入不同才显示，省得用户看重复信息）。</summary>
    [ObservableProperty]
    private string _dictionaryWord = "";

    /// <summary>命中的那份词典名（FR-055：多词典时「谁给的释义」必须可见）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DictionaryHeader))]
    private string _dictionarySource = "";

    /// <summary>词典卡标题：装了多本时要说清是哪本给的（FR-055）。</summary>
    public string DictionaryHeader => DictionarySource.Length == 0 ? "词典" : $"词典 · {DictionarySource}";

    /// <summary>词典释义是否展开全文（卡片默认截断，点一下看全）。</summary>
    [ObservableProperty]
    private bool _isDictionaryExpanded;

    /// <summary>词典卡是否可见。</summary>
    public bool IsDictionaryVisible => DictionaryDefinition.Length > 0;

    /// <summary>
    /// 查一次词典（FR-049 本地 mdx + FR-056 AI 兜底）。只在译文成功落地后跑，
    /// 且**在 await 处回到 UI 线程**再改属性。两者都缺席（开关全关 / 非单词）→ 直接清零不查。
    /// </summary>
    private async Task LookUpDictionaryAsync(string source, string translated)
    {
        var mdxAllowed = _dictionaries is not null && _settings.DictionariesEnabled;
        var aiAllowed = _settings.AiDictionaryEnabled;
        if ((!mdxAllowed && !aiAllowed) || !IsSingleWord(source))
        {
            DictionaryDefinition = "";
            DictionaryWord = "";
            DictionarySource = "";
            return;
        }

        try
        {
            // 首次查词会把资源块解到缓存目录（上百毫秒），AI 词典更是一次网络往返 ——
            // 全放后台，别把译文落地的节奏拖住
            var hit = await Task.Run(() =>
            {
                if (mdxAllowed)
                {
                    var local = _dictionaries!.Query(source)
                        ?? (IsSingleWord(translated) ? _dictionaries.Query(translated) : null);
                    if (local is not null)
                    {
                        return new DictionaryCard(local.Word, local.Definition, local.SourceName);
                    }
                }

                var entry = aiAllowed ? QueryAiDictionary(source) : null;
                return entry is null
                    ? null
                    : new DictionaryCard(entry.Wordhead, entry.Definition, "AI 词典");
            });

            // 等结果期间用户可能已经改写输入或换了会话：过期结果直接丢弃，不给小窗挂上不相干的释义
            if (!IsSingleWord(InputText) || !string.Equals(InputText.Trim(), source, StringComparison.Ordinal))
            {
                return;
            }

            if (hit is null)
            {
                DictionaryDefinition = "";
                DictionaryWord = "";
                DictionarySource = "";
                IsDictionaryExpanded = false;
                return;
            }

            DictionaryWord = string.Equals(hit.Word, source, StringComparison.OrdinalIgnoreCase) ? "" : hit.Word;
            DictionaryDefinition = hit.Definition;
            DictionarySource = hit.Source ?? "";
            IsDictionaryExpanded = false;
        }
        catch (Exception ex)
        {
            // 词典只是加分项：任何异常都不该影响已落地的译文
            Log.Debug(ex, "本地词典查询失败（忽略）");
        }
    }

    /// <summary>词典卡的一行数据（本地 mdx 与 AI 词典共用）。</summary>
    private sealed record DictionaryCard(string Word, string Definition, string? Source);

    /// <summary>
    /// AI 词典兜底（FR-056）：只有当前引擎是 AI 且已配置时才发这次额外请求，
    /// 走 <see cref="GlossaryTranslator.Unwrap"/> 后的裸引擎——词典答案不是译文，
    /// 既不该被术语表改写，也不该计入引擎看板（会让 P50 与失败率失真）。
    /// 8 秒拿不到就放弃：这是卡片，不是用户等的答案。
    /// </summary>
    private AiDictionaryEntry? QueryAiDictionary(string word)
    {
        if (GlossaryTranslator.Unwrap(_catalog.Resolve(_settings.Engine))
            is not LlmTranslator { IsConfigured: true } llm)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(AiDictionaryTimeout);
            return llm.QueryDictionaryAsync(word, TargetLanguage, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.Debug("AI 词典超时（{Timeout}ms），本次不显示词典卡", (int)AiDictionaryTimeout.TotalMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            // 只记异常类型，不记词面（需求 6 日志脱敏）
            Log.Debug("AI 词典查询失败（{Type}），忽略", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>AI 词典请求的等待上限：拿不到就不显示卡片，绝不拖住词典线程。</summary>
    private static readonly TimeSpan AiDictionaryTimeout = TimeSpan.FromSeconds(8);

    /// <summary>「单词」判据：≤32 字符且无换行（spec §7：划词结果为单词时才出词典卡）。</summary>
    private static bool IsSingleWord(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 32 && !text.Contains('\n') && !text.Contains('\r');

    /// <summary>展开 / 收起词典释义。</summary>
    [RelayCommand]
    private void ToggleDictionaryExpanded() => IsDictionaryExpanded = !IsDictionaryExpanded;

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

        // FR-058：用户在某个应用里手动改了语言 = 这就是他下次想要的，记住它
        if (!string.IsNullOrEmpty(value) && _settings.AppLanguageMemoryEnabled && _foregroundApp.Length > 0)
        {
            _settings.AppLanguagePairs = AppLanguageRules.Learn(
                _settings.AppLanguagePairs, _foregroundApp, SourceLanguage, value).ToList();
            _store.Save(_settings);
            Log.Debug("按应用语言对已记忆（会话级命中，不记录进程名）");
        }

        // 13.4.1：语言变更后已有对比结果不再对应当前语言对，清空并提示需重新对比
        ClearCompareResultsForLanguageChange();
    }

    /// <summary>
    /// 记录本次会话的前台应用（FR-058），并在这一步套用「按应用语言对」规则。
    /// 命中只改**本次会话**的语言：改全局默认会让用户切个浏览器就发现设置被偷偷动过。
    /// </summary>
    public void SetForegroundApp(string? processName)
    {
        _foregroundApp = AppLanguageRules.Normalize(processName);
        if (_foregroundApp.Length == 0
            || !_settings.AppLanguageMemoryEnabled
            || AppLanguageRules.Match(_settings.AppLanguagePairs, _foregroundApp) is not { } rule)
        {
            return;
        }

        // 有意绕开属性 setter：setter 会把会话级选择写进全局设置并落盘
#pragma warning disable MVVMTK0034
        _sourceLanguage = string.IsNullOrWhiteSpace(rule.SourceLanguage)
            ? TranslationLanguages.AutoCode : rule.SourceLanguage;
        OnPropertyChanged(nameof(SourceLanguage));
        if (!string.IsNullOrWhiteSpace(rule.TargetLanguage))
        {
            _targetLanguage = rule.TargetLanguage;
            OnPropertyChanged(nameof(TargetLanguage));
        }
#pragma warning restore MVVMTK0034

        // 日志只记"命中"，不记进程名与语言对（B5 红线：这类信息不该进可外传的日志文件）
        Log.Debug("按应用语言对命中，本次会话语言已套用");
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

        // 批 5：换说法能力取决于当前引擎（档案切换可能刚换掉引擎），风格按钮随会话现算
        RefreshStyleSupport();

        // FR-053：呼出新会话时停掉上一轮的跟读（逐句列表随会话作废）
        StopShadow();

        // FR-052：每天首次呼出小窗时给一条复习词（开关关 → 立刻返回，不做任何 IO）
        _ = RefreshDailyReviewAsync();

        // FR-049：词典卡随会话清空（在途查询靠输入比对判过期，不会把上一句的释义留下）
        DictionaryDefinition = "";
        DictionaryWord = "";
        DictionarySource = "";
        IsDictionaryExpanded = false;

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
        SupportsStyle = TranslatorCatalog.SupportsDirectives(translator);

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
            var result = await TranslateWithDirectiveAsync(translator, text, BuildDirective(translator, text));
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
    /// <param name="recordHistory">FR-051：换说法的重请求不写历史（同句多译文会污染 TM 候选池）。</param>
    /// <param name="autoSpeak">FR-051：换说法时用户在看着译文，不再朗读一遍原文。</param>
    private void ApplySuccess(
        ITranslator translator, string text, TranslationResult result,
        bool recordHistory = true, bool autoSpeak = true)
    {
        ResultText = result.TranslatedText;
        _ = LookUpDictionaryAsync(text, result.TranslatedText);
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
        if (recordHistory && !_settings.PrivacyMode)
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
        if (autoSpeak)
        {
            TryAutoSpeakSource(text);
        }
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
        _ = LookUpDictionaryAsync(text, hit.Entry.Translated);
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
    [NotifyPropertyChangedFor(nameof(IsShadowButtonVisible))]
    private bool _isAlignView;

    /// <summary>整块译文区是否显示（对照 / 跟读打开时隐藏——同一区域三种竖排互斥）。</summary>
    public bool IsBlockViewVisible => !IsAlignView && !IsShadowMode;

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

    // ==================== 批 5 5a：AI 语境化（FR-050）/ 换说法（FR-051） ====================

    /// <summary>
    /// 「换说法」按钮组是否可用（FR-051）：只有 AI 引擎有 Prompt 通道。
    /// 官方引擎一律不显示——不做「点了没反应」的假支持。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyStyleCommand))]
    private bool _supportsStyle;

    /// <summary>当前风格（设置项的镜像，按钮高亮用）；点已激活的按钮 = 取消。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStyleColloquial))]
    [NotifyPropertyChangedFor(nameof(IsStyleFormal))]
    [NotifyPropertyChangedFor(nameof(IsStyleConcise))]
    private string _activeStyleKey = "none";

    public bool IsStyleColloquial => ActiveStyleKey == "colloquial";
    public bool IsStyleFormal => ActiveStyleKey == "formal";
    public bool IsStyleConcise => ActiveStyleKey == "concise";

    /// <summary>每次呼出 / 每次翻译后刷新：档案切换可能刚把引擎换成或换成非 AI。</summary>
    private void RefreshStyleSupport()
    {
        SupportsStyle = TranslatorCatalog.SupportsDirectives(_catalog.Resolve(_settings.Engine));
        ActiveStyleKey = TranslationStyles.Parse(_settings.TranslationStyle).ToSettingKey();
        // 跟读入口看的是设置里的开关（用户可能刚在设置页改过），每次呼出现算
        OnPropertyChanged(nameof(IsShadowButtonVisible));
        ToggleShadowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 组装本次请求的指令（FR-050 / FR-051）：非 AI 引擎直接给 <see cref="TranslationDirective.None"/>；
    /// 语境只在「语境开关开 + 非隐私 + 同语言对 30 分钟内有别的原文」时携带——
    /// 那段文本本来就在同一引擎的历史出网请求里，不因此多送一个新内容。
    /// </summary>
    private TranslationDirective BuildDirective(ITranslator translator, string text)
    {
        if (!TranslatorCatalog.SupportsDirectives(translator))
        {
            return TranslationDirective.None;
        }

        var style = TranslationStyles.Parse(_settings.TranslationStyle);
        string? context = null;
        if (_settings.LlmContextEnabled && !_settings.PrivacyMode)
        {
            try
            {
                context = _history.ContextSource(TargetLanguage, text);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "读取语境失败，本次按无语境翻译");
            }
        }

        if (context is not null)
        {
            // 只记长度，绝不记录语境内容（需求 6 日志脱敏）
            Log.Debug("AI 语境已携带 {Length} 字符", context.Length);
        }

        return new TranslationDirective(context, style);
    }

    private Task<TranslationResult> TranslateWithDirectiveAsync(
        ITranslator translator, string text, TranslationDirective directive) =>
        translator is IPromptDirectiveTranslator directable
            ? directable.TranslateAsync(text, SourceLanguage, TargetLanguage, directive)
            : translator.TranslateAsync(text, SourceLanguage, TargetLanguage);

    /// <summary>
    /// 换说法（FR-051）：同一原文带新风格重新请求，成功即替换译文；点已激活的按钮 = 取消风格。
    /// 不写历史（同句多译会污染 TM 候选池）、不自动朗读、强制跳过 TM 回填（否则风格打不进回填的译文）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyStyle))]
    private async Task ApplyStyleAsync(string? styleKey)
    {
        var current = TranslationStyles.Parse(ActiveStyleKey);
        var requested = TranslationStyles.Parse(styleKey);
        var target = requested == current ? TranslationStyle.None : requested;

        ActiveStyleKey = target.ToSettingKey();
        _settings.TranslationStyle = ActiveStyleKey;
        _store.Save(_settings);

        var text = InputText?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        var translator = _catalog.Resolve(_settings.Engine);
        IsBusy = true;
        ErrorText = "";
        StatusText = target == TranslationStyle.None ? "恢复原样…" : $"{target.DisplayName()}…";
        try
        {
            var result = await TranslateWithDirectiveAsync(translator, text, BuildDirective(translator, text));
            ApplySuccess(translator, text, result, recordHistory: false, autoSpeak: false);
            StatusText = target == TranslationStyle.None ? "已恢复原样" : $"已换说法：{target.DisplayName()}";
        }
        catch (TranslationException ex)
        {
            ReportFailure(translator, ex);
        }
        catch (Exception ex)
        {
            ErrorText = "发生未知错误";
            StatusText = "";
            Log.Error(ex, "换说法出现未预期异常（引擎={Engine}）", translator.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyStyle() => SupportsStyle && HasResult && !IsBusy;

    // ==================== 批 5 5b：影子跟读（FR-053）/ 每日复习（FR-052） ====================

    /// <summary>跟读的一行。<see cref="IsCurrent"/> 决定高亮——用可通知对象而不是重建整表，避免每句刷新布局。</summary>
    public sealed partial class ShadowLineView : ObservableObject
    {
        public required string Text { get; init; }

        [ObservableProperty]
        private bool _isCurrent;
    }

    public ObservableCollection<ShadowLineView> ShadowLines { get; } = [];

    /// <summary>跟读模式（译文按句竖排）：与整块、对照视图互斥。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlockViewVisible))]
    [NotifyPropertyChangedFor(nameof(IsAlignButtonVisible))]
    private bool _isShadowMode;

    /// <summary>正在跟读中（按钮文案 = 停止）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleShadowCommand))]
    private bool _isShadowRunning;

    /// <summary>当前念到第几句（-1 = 未在跟读）。</summary>
    [ObservableProperty]
    private int _shadowIndex = -1;

    private CancellationTokenSource? _shadowCts;

    /// <summary>今日复习行文案（「apple → 苹果」）；空 = 不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReviewVisible))]
    [NotifyCanExecuteChangedFor(nameof(NextReviewWordCommand))]
    private string _reviewText = "";

    /// <summary>复习进度「2/5」。</summary>
    [ObservableProperty]
    private string _reviewProgress = "";

    public bool IsReviewVisible => ReviewText.Length > 0;

    private IReadOnlyList<VocabularyEntry> _reviewWords = [];
    private IReadOnlyList<int> _reviewIndices = [];

    private bool CanToggleShadow() => _settings.ShadowReadingEnabled && CanSpeak && HasResult;

    /// <summary>「跟读」按钮是否出现（FR-053：默认关，在「设置 → 高级 → 朗读」里开）；对照打开时让位。</summary>
    public bool IsShadowButtonVisible => _settings.ShadowReadingEnabled && CanSpeak && !IsAlignView;

    /// <summary>
    /// 跟读（FR-053）：译文按句竖排，SAPI 逐句念完一句再念下一句，句间停顿留给用户跟着念。
    /// 再点一次 / 关窗 / 呼出新会话 = 停止。只用系统语音，不联网、不外发任何文本。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleShadow))]
    private async Task ToggleShadowAsync()
    {
        if (_shadowCts is not null)
        {
            StopShadow();
            return;
        }

        var sentences = SentenceSplitter.Split(ResultText);
        if (sentences.Count == 0)
        {
            return;
        }

        var views = sentences.Select(s => new ShadowLineView { Text = s }).ToArray();
        foreach (var view in views)
        {
            ShadowLines.Add(view);
        }

        IsAlignView = false; // 逐句列表与段落对照共用一格，开跟读前先让对照让位
        IsShadowMode = true;
        IsShadowRunning = true;
        // 只记句数，不记内容（需求 6 日志脱敏）
        Log.Debug("影子跟读开始：{Count} 句，停顿 {Pause}ms", views.Length, Math.Max(0, _settings.ShadowPauseMs));
        _shadowCts = new CancellationTokenSource();
        var token = _shadowCts.Token;
        var pauseMs = Math.Max(0, _settings.ShadowPauseMs);
        try
        {
            for (var i = 0; i < views.Length; i++)
            {
                views[i].IsCurrent = true;
                ShadowIndex = i;
                await _tts.SpeakAndWaitAsync(views[i].Text, TargetLanguage, token);
                if (pauseMs > 0)
                {
                    await Task.Delay(pauseMs, token);
                }

                views[i].IsCurrent = false;
            }

            StatusText = "跟读结束";
        }
        catch (OperationCanceledException)
        {
            // 用户点了停止或换了会话：正常收尾，不算错误
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "跟读中断（语音环境问题不影响其它功能）");
        }
        finally
        {
            foreach (var view in views)
            {
                view.IsCurrent = false;
            }

            _shadowCts?.Dispose();
            _shadowCts = null;
            IsShadowRunning = false;
            IsShadowMode = false;
            ShadowIndex = -1;
            ShadowLines.Clear();
        }
    }

    /// <summary>停止跟读：取消循环并立刻静音（SAPI 的取消是异步的，先 Stop 才不会念完当前句）。</summary>
    private void StopShadow()
    {
        _shadowCts?.Cancel();
        _tts.Stop();
    }

    /// <summary>窗口隐藏（Esc / 点外部 / 关窗）即停：语音不该在看不见的窗口里继续念。</summary>
    public void CancelShadowReading() => StopShadow();

    /// <summary>
    /// 刷新今日复习行（FR-052）：开关关 / 没收藏 / 今天已看完 → 不显示。
    /// 取词放后台（生词本可能上千条），但复习行不是译文链路的一部分，晚几十毫秒无感。
    /// </summary>
    private async Task RefreshDailyReviewAsync()
    {
        if (!_settings.DailyReviewEnabled)
        {
            _reviewIndices = [];
            ReviewText = "";
            ReviewProgress = "";
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var key = today.ToString("yyyy-MM-dd");
        if (!string.Equals(_settings.DailyReviewDate, key, StringComparison.Ordinal))
        {
            _settings.DailyReviewDate = key;
            _settings.DailyReviewIndex = 0;
            _store.Save(_settings);
        }

        try
        {
            _reviewWords = await Task.Run(_vocabulary.List);
            _reviewIndices = DailyReviewSelector.Pick(today, _reviewWords.Count);
            ShowReviewWord();
        }
        catch (Exception ex)
        {
            // 复习是提醒，不是功能主线：库读不出来就当没有，绝不影响小窗
            Log.Debug(ex, "每日复习取词失败（忽略）");
        }
    }

    private void ShowReviewWord()
    {
        var position = _settings.DailyReviewIndex;
        if (_reviewIndices.Count == 0 || position >= _reviewIndices.Count)
        {
            ReviewText = "";
            ReviewProgress = "";
            return;
        }

        var word = _reviewWords[_reviewIndices[position]];
        ReviewText = $"{word.SourceText} → {word.TranslatedText}";
        ReviewProgress = $"{position + 1}/{_reviewIndices.Count}";
    }

    private bool CanNextReviewWord() => IsReviewVisible;

    [RelayCommand(CanExecute = nameof(CanNextReviewWord))]
    private void NextReviewWord()
    {
        _settings.DailyReviewIndex++;
        _store.Save(_settings);
        Log.Debug("每日复习：切到第 {Position} 条", _settings.DailyReviewIndex + 1);
        ShowReviewWord();
    }

    /// <summary>「今天到这」：把今天的进度推到底，明天自然回到第 1 条。</summary>
    [RelayCommand]
    private void FinishReviewToday()
    {
        _settings.DailyReviewIndex = _reviewIndices.Count;
        _store.Save(_settings);
        Log.Debug("每日复习：今天到此结束（共 {Count} 条）", _reviewIndices.Count);
        ShowReviewWord();
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
        TryPushToAnki(source, WithDictionary(ResultText));
    }

    /// <summary>
    /// FR-049：卡片上显示的词典释义并入 Anki 背面字段（生词本本身仍只存引擎译文，
    /// 免得离线词典的版权文本被写进历史记录）。
    /// </summary>
    private string WithDictionary(string translated) =>
        DictionaryDefinition.Length == 0 ? translated : $"{translated}\n{DictionaryDefinition}";

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
    [NotifyPropertyChangedFor(nameof(IsAlignButtonVisible))]
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

    /// <summary>
    /// 「对照」（逐段原文/译文）按钮是否出现。译文区同一格叠着三种竖排（整块 / 段落对照 /
    /// 跟读），再加多引擎对比栏——谁都可能压在谁上面，所以入口按「别人没开」给。
    /// </summary>
    public bool IsAlignButtonVisible => HasAlignment && !IsComparing && !IsShadowMode;

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
        IsAlignView = false; // 段落对照与对比栏共用一格：进对比必须先让位，否则两套文字叠在一起

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
