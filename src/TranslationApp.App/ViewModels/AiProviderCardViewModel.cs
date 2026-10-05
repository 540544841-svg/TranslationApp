using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>Wire API 下拉项（值 + 展示名 + 说明）。</summary>
public sealed record WireApiOption(LlmWireApi Value, string Label, string Hint);

/// <summary>两种 wire API 的下拉候选（卡片与档位行共用一份，避免每次取属性都重建集合）。</summary>
internal static class WireApiCatalog
{
    internal static readonly IReadOnlyList<WireApiOption> All =
    [
        new(LlmWireApi.Chat, "chat", "对话补全 /chat/completions"),
        new(LlmWireApi.Responses, "responses", "Responses API /responses"),
    ];
}

/// <summary>
/// 「引擎」页 AI 供应商区的一张档位卡片（FR-022）：档位胶囊行 + 选中档的编辑区 + 底部功能栏。
/// 一个引擎、多档配置：家里 Ollama、公司网关、云上 API 并存，切「当前档」即换供应商。
/// </summary>
public sealed partial class AiProviderCardViewModel : ObservableObject
{
    /// <summary>「测试连接」固定测试文本（13.1.6：hello，en → zh-CN）。</summary>
    private const string TestText = "hello";
    private const string TestSource = "en";
    private const string TestTarget = "zh-CN";

    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action _onChanged;
    private readonly LlmTranslator _translator;
    private readonly EngineStatsRepository? _stats;

    /// <summary>构造期回填不落盘（与既有卡片同一约定）。</summary>
    private bool _loading = true;

    internal AiProviderCardViewModel(
        AppSettings settings,
        Action<AppSettings> save,
        Action onChanged,
        LlmTranslator translator,
        EngineStatsRepository? stats = null)
    {
        _settings = settings;
        _save = save;
        _onChanged = onChanged;
        _translator = translator;
        _stats = stats;

        foreach (var provider in settings.LlmProviders)
        {
            Providers.Add(new AiProviderItemViewModel(provider, settings, save, OnProviderEdited));
        }

        _selected = Providers.FirstOrDefault(p => p.Id == settings.LlmActiveProviderId) ?? Providers.FirstOrDefault();
        _prompt = settings.LlmPrompt;
        _temperature = settings.LlmTemperature;
        _loading = false;
        RefreshActiveFlags();
        RefreshStats();
    }

    /// <summary>全部档位（横向胶囊行）。</summary>
    public ObservableCollection<AiProviderItemViewModel> Providers { get; } = [];

    /// <summary>当前编辑的档（胶囊行里那枚被选中的）。</summary>
    [ObservableProperty]
    private AiProviderItemViewModel? _selected;

    public IReadOnlyList<WireApiOption> WireApis => WireApiCatalog.All;

    public bool HasProviders => Providers.Count > 0;

    /// <summary>只剩一档时不许删（列表空了就没有「当前供应商」可用）。</summary>
    public bool CanRemoveProvider => Providers.Count > 1;

    partial void OnSelectedChanged(AiProviderItemViewModel? value)
    {
        TestMessage = "";
        FetchModelsCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedIsActive));
        OnPropertyChanged(nameof(SelectedHint));
    }

    // ==================== 卡片头 / 底部功能栏 ====================

    /// <summary>是否当前引擎：设计稿在那一张印面上盖一枚朱砂「当前引擎」。</summary>
    [ObservableProperty]
    private bool _isCurrentEngine;

    public string StatusText => _translator.IsConfigured ? "已配置" : "未配置";

    /// <summary>工作台右栏那列要的是「现在能不能用」，与「引擎」页的「已配置」不同。</summary>
    public string WorkbenchStatusText => _translator.IsConfigured ? "在线" : "未配置";

    public bool IsOnline => _translator.IsConfigured;

    /// <summary>功能栏里的状态标签：当前档已配置才算可用。</summary>
    public string FooterTagText => _translator.IsConfigured ? "已配置" : "未配置";

    public bool FooterTagPositive => _translator.IsConfigured;

    public string FooterHint => _translator.IsConfigured
        ? $"当前档：{ActiveProviderName()}"
        : "填好接口地址与模型名即算配置完成";

    /// <summary>选中的这档是不是「当前档」——测试连接只测当前档。</summary>
    public bool SelectedIsActive => Selected is not null && Selected.Id == _settings.LlmActiveProviderId;

    public string SelectedHint => SelectedIsActive
        ? "测试连接会真发一次请求（hello，英译中）"
        : "测试连接只测当前档；先点「设为当前」再测";

    /// <summary>近 7 天 P50 耗时（毫秒）；无样本为 null。由 <see cref="RefreshStats"/> 回填。</summary>
    internal double? P50Ms { get; private set; }

    /// <summary>工作台右栏「引擎」列表的耗时列；无样本显示破折号。</summary>
    public string LatencyText => P50Ms is { } ms ? (ms < 1000 ? $"{ms:0}ms" : $"{ms / 1000:0.#}s") : "—";

    /// <summary>P0 批 1 / spec §4.3：近 7 天成败看板文本（无数据为空并隐藏整行）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private string _statsText = "";

    public bool HasStats => StatsText.Length > 0;

    /// <summary>切到「引擎」页时刷新看板（统计由翻译链路的装饰器写入）。</summary>
    internal void RefreshStats()
    {
        var s = _stats?.GetSummary("llm") ?? new EngineStatsSummary(0, 0, 0, 0, 0, 0, "");
        P50Ms = s.P50Ms;
        OnPropertyChanged(nameof(LatencyText));

        if (s.IsEmpty)
        {
            StatsText = "";
            return;
        }

        var parts = new List<string> { $"近 7 天：成功 {s.Success}" };
        if (s.FailTotal > 0)
        {
            var detail = new List<string>();
            if (s.FailNetwork > 0) detail.Add($"网络 {s.FailNetwork}");
            if (s.FailQuota > 0) detail.Add($"配额 {s.FailQuota}");
            if (s.FailKey > 0) detail.Add($"密钥 {s.FailKey}");
            if (s.FailEngine > 0) detail.Add($"接口 {s.FailEngine}");
            parts.Add($"失败 {s.FailTotal}（{string.Join(" / ", detail)}）");
        }
        if (s.FallbackUsed > 0) parts.Add($"已自动降级 {s.FallbackUsed}");
        if (s.P50Ms is { } p50) parts.Add($"P50 {LatencyText}");
        if (s.LastError.Length > 0 && s.FailTotal > 0) parts.Add($"最近失败：{s.LastError}");

        StatsText = string.Join(" · ", parts);
    }

    /// <summary>当前档的显示名（没有档时给个兜底文案）。</summary>
    private string ActiveProviderName()
    {
        var active = Providers.FirstOrDefault(p => p.Id == _settings.LlmActiveProviderId);
        return active?.ChipLabel ?? "未命名";
    }

    /// <summary>密钥 / 地址 / 档位变化后：刷新状态与下拉可用性。</summary>
    private void OnProviderEdited()
    {
        TestMessage = "";
        RefreshActiveFlags();
        RefreshDerived();
        _onChanged();
    }

    internal void RefreshDerived()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WorkbenchStatusText));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(FooterTagText));
        OnPropertyChanged(nameof(FooterTagPositive));
        OnPropertyChanged(nameof(FooterHint));
        OnPropertyChanged(nameof(CanRemoveProvider));
        OnPropertyChanged(nameof(HasProviders));
        RemoveProviderCommand.NotifyCanExecuteChanged();
        FetchModelsCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    private void RefreshActiveFlags()
    {
        foreach (var provider in Providers)
        {
            provider.IsActive = provider.Id == _settings.LlmActiveProviderId;
        }
    }

    // ==================== 档位操作 ====================

    [RelayCommand]
    private void SelectProvider(AiProviderItemViewModel? item)
    {
        if (item is not null)
        {
            Selected = item;
        }
    }

    [RelayCommand]
    private void AddProvider()
    {
        var provider = LlmProvider.CreateBlank();
        provider.Name = $"供应商 {Providers.Count + 1}";
        _settings.LlmProviders.Add(provider);
        Save();

        var item = new AiProviderItemViewModel(provider, _settings, _save, OnProviderEdited);
        Providers.Add(item);
        Selected = item;
        RefreshDerived();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveProvider))]
    private void RemoveProvider()
    {
        if (Selected is not { } item || Providers.Count <= 1)
        {
            return;
        }

        var index = Providers.IndexOf(item);
        Providers.Remove(item);
        _settings.LlmProviders.RemoveAll(p => p.Id == item.Id);

        // 删掉的是当前档 → 顺位补一个，保证「当前档」永远指向存在的档
        if (_settings.LlmActiveProviderId == item.Id)
        {
            _settings.LlmActiveProviderId = Providers[0].Id;
        }

        Save();
        Selected = Providers[Math.Clamp(index, 0, Providers.Count - 1)];
        RefreshActiveFlags();
        RefreshDerived();
        _onChanged();
    }

    /// <summary>把这一档设为当前档，并把「当前引擎」切到 AI（否则测试连接测的还是别的档）。</summary>
    [RelayCommand]
    private void UseProvider()
    {
        if (Selected is not { } item)
        {
            return;
        }

        _settings.LlmActiveProviderId = item.Id;
        _settings.Engine = "llm";
        Save();
        RefreshActiveFlags();
        RefreshDerived();
        OnPropertyChanged(nameof(SelectedIsActive));
        OnPropertyChanged(nameof(SelectedHint));
        _onChanged();
    }

    // ==================== 模型列表 ====================

    private bool CanFetchModels() => Selected is { } item && item.BaseUrl.Trim().Length > 0;

    /// <summary>向选中档的 <c>GET {base}/v1/models</c> 要一次模型列表（拉的是选中档，不必先设为当前）。</summary>
    [RelayCommand(CanExecute = nameof(CanFetchModels))]
    private async Task FetchModelsAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }

        item.IsFetchingModels = true;
        item.ModelFetchMessage = "";
        try
        {
            var models = await _translator.ListModelsAsync(item.Provider);
            item.ApplyFetchedModels(models);
            Log.Information("获取模型列表：{Provider} → {Count} 个", item.ChipLabel, models.Count);
        }
        catch (TranslationException ex)
        {
            item.ModelFetchMessage = ex.Message;
            Log.Warning("获取模型列表失败：{Provider}，错误类型 {ErrorType}", item.ChipLabel, ex.ErrorType);
        }
        catch (Exception ex)
        {
            item.ModelFetchMessage = "获取失败：发生未预期错误，详见日志";
            Log.Error(ex, "获取模型列表出现未预期异常（供应商={Provider}）", item.ChipLabel);
        }
        finally
        {
            item.IsFetchingModels = false;
        }
    }

    // ==================== 测试连接 ====================

    private bool CanTest() => SelectedIsActive && _translator.IsConfigured;

    /// <summary>发一次真实翻译请求验证当前档（13.1.6）。</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestMessage = "";
        var stopwatch = Stopwatch.StartNew();

        // P0 批 1 / spec §4.2：「测试连接」不是真实翻译，整段抑制引擎看板计数
        using (GlossaryTranslator.SuppressStats())
        try
        {
            var result = await _translator.TranslateAsync(TestText, TestSource, TestTarget);
            stopwatch.Stop();

            var translated = result.TranslatedText.Trim().ReplaceLineEndings(" ");
            TestSucceeded = true;
            TestMessage = $"连接成功：译文「{translated}」，耗时 {stopwatch.Elapsed.TotalSeconds:0.0}s";
            Log.Information("测试连接成功：AI（{Provider}），{Elapsed} ms", ActiveProviderName(), stopwatch.ElapsedMilliseconds);
        }
        catch (TranslationException ex)
        {
            TestSucceeded = false;
            TestMessage = DescribeFailure(ex);
            Log.Warning("测试连接失败：AI，错误类型 {ErrorType}", ex.ErrorType);
        }
        catch (Exception ex)
        {
            TestSucceeded = false;
            TestMessage = "连接失败：发生未预期错误，详见日志";
            Log.Error(ex, "测试连接出现未预期异常（AI）");
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>失败文案：先按错误分类给出结论，再附引擎原文（13.1.6 要求区分三类失败）。</summary>
    private string DescribeFailure(TranslationException ex)
    {
        var head = ex.ErrorType switch
        {
            TranslationErrorType.InvalidKey => "连接失败：密钥无效或未授权",
            TranslationErrorType.QuotaExceeded => "连接失败：额度用尽或触发限流",
            TranslationErrorType.Network => "连接失败：网络不可达（可在「高级」页开启代理后重试）",
            _ => "连接失败：引擎异常",
        };

        var detail = Redact(ex.Message).Trim();
        return detail.Length == 0 ? head : $"{head}（{detail}）";
    }

    /// <summary>展示前过滤明文密钥：部分服务会把凭据回显在错误消息里（13.1.6 安全要求）。</summary>
    private string Redact(string text)
    {
        foreach (var provider in Providers)
        {
            if (provider.ApiKey.Length >= 6)
            {
                text = text.Replace(provider.ApiKey, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    // ==================== 高级选项（对全部档生效）====================

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    /// <summary>自定义系统 Prompt；留空 = 使用内置（13.3.2）。</summary>
    [ObservableProperty]
    private string _prompt = "";

    /// <summary>采样温度（0~2）。</summary>
    [ObservableProperty]
    private double _temperature;

    /// <summary>折叠区内的反馈文案（如「已恢复内置 Prompt」）。</summary>
    [ObservableProperty]
    private string _advancedMessage = "";

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    /// <summary>自定义请求头折叠区（内网网关 / 组织标识这类额外头）。</summary>
    [ObservableProperty]
    private bool _isHeadersExpanded;

    [RelayCommand]
    private void ToggleHeaders() => IsHeadersExpanded = !IsHeadersExpanded;

    /// <summary>恢复内置 Prompt（13.3.2「留空即用内置」）。</summary>
    [RelayCommand]
    private void ResetPrompt()
    {
        Prompt = "";
        AdvancedMessage = "已恢复内置 Prompt（只输出译文）";
    }

    partial void OnPromptChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _settings.LlmPrompt = value ?? "";
        Save();
        TestMessage = "";
        AdvancedMessage = "";
    }

    partial void OnTemperatureChanged(double value)
    {
        if (_loading)
        {
            return;
        }

        _settings.LlmTemperature = value;
        Save();
        TestMessage = "";
        AdvancedMessage = "";
    }

    // ==================== 测试连接的结果呈现 ====================

    [ObservableProperty]
    private string _testMessage = "";

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private bool _isTesting;

    private void Save() => _save(_settings);
}

/// <summary>
/// AI 供应商列表里的一档（FR-022）：往 <see cref="AppSettings.LlmProviders"/> 里的那个对象上直接写，
/// 因此「变更即保存」的既有约定不变，无需额外的应用按钮。
/// </summary>
public sealed partial class AiProviderItemViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private readonly Action _onChanged;
    private readonly LlmProvider _provider;
    private readonly bool _loading;

    internal AiProviderItemViewModel(
        LlmProvider provider,
        AppSettings settings,
        Action<AppSettings> save,
        Action onChanged)
    {
        _provider = provider;
        _settings = settings;
        _save = save;
        _onChanged = onChanged;

        _loading = true;
        _name = provider.Name;
        _baseUrl = provider.BaseUrl;
        _modelName = provider.Model;
        _wireApi = provider.WireApi;
        _requiresAuth = provider.RequiresAuth;
        _apiKey = SecretStore.Unprotect(provider.ApiKeyEncrypted) ?? "";
        _extraHeaders = provider.ExtraHeadersJson;
        foreach (var model in provider.CustomModels)
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                CustomModels.Add(model.Trim());
            }
        }

        _loading = false;
        SyncModelOptions();

        // 下拉里若有与当前模型同名的候选，先选中它：闭合态就不是一个空框（直接写字段，免得回写模型名再存一次盘）
        if (_pickedModel.Length == 0 && ModelName.Trim().Length > 0)
        {
            var match = ModelOptions.FirstOrDefault(
                model => string.Equals(model, ModelName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _pickedModel = match;
            }
        }
    }

    /// <summary>底层档位对象（「获取模型列表」要拿它去问接口）。</summary>
    public LlmProvider Provider => _provider;

    public string Id => _provider.Id;

    public IReadOnlyList<WireApiOption> WireApis => WireApiCatalog.All;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _baseUrl = "";

    /// <summary>AI 模型名（与 <see cref="Provider"/> 区分：那是整档配置）。</summary>
    [ObservableProperty]
    private string _modelName = "";

    [ObservableProperty]
    private LlmWireApi _wireApi = LlmWireApi.Chat;

    [ObservableProperty]
    private bool _requiresAuth = true;

    /// <summary>明文密钥（只在内存与界面上；落盘经 DPAPI）。</summary>
    [ObservableProperty]
    private string _apiKey = "";

    /// <summary>自定义请求头，JSON 对象文本。</summary>
    [ObservableProperty]
    private string _extraHeaders = "";

    /// <summary>是否当前档（胶囊上那枚朱砂印）。</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>胶囊上显示的名字（空档位给个占位）。</summary>
    public string ChipLabel => string.IsNullOrWhiteSpace(Name) ? "未命名" : Name.Trim();

    // ---- 「获取模型列表」的状态 ----

    // ---- 模型候选：「自建」（落盘保留）+「接口拉回」（本次会话）----

    /// <summary>下拉里的候选：自建在前、接口拉回的在后（见 <see cref="LlmProvider.MergeModels"/>）。</summary>
    public ObservableCollection<string> ModelOptions { get; } = [];

    /// <summary>用户手填后「加入列表」的自建候选，落盘在 <see cref="LlmProvider.CustomModels"/>。</summary>
    public ObservableCollection<string> CustomModels { get; } = [];

    /// <summary>接口 <c>GET /models</c> 拉回的候选（不落盘，重开就没了）。</summary>
    public ObservableCollection<string> FetchedModels { get; } = [];

    [ObservableProperty]
    private bool _hasModelOptions;

    /// <summary>正在从下拉里删自建条目：期间 ComboBoxItem 抢到的选中不写回模型框。</summary>
    private bool _removingCustomModel;

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private string _modelFetchMessage = "";

    /// <summary>有提示文字才显示那一行（BoolToVisibility 要 bool）。</summary>
    public bool HasModelFetchMessage => ModelFetchMessage.Length > 0;

    partial void OnModelFetchMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasModelFetchMessage));

    /// <summary>把接口拉回的候选换成新的（丢掉上一次的），并刷新下拉。</summary>
    internal void ApplyFetchedModels(IReadOnlyList<string> models)
    {
        FetchedModels.Clear();
        foreach (var model in models)
        {
            FetchedModels.Add(model);
        }

        SyncModelOptions();
        ModelFetchMessage = FetchedModels.Count > 0
            ? $"拿到 {FetchedModels.Count} 个模型，选一个即填入模型名"
            : "接口没返回模型列表；手填模型名后点「加入列表」攼一份";
    }

    /// <summary>把当前手填的模型名加进自建列表（已在列表里就不重复加）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddToCustomList))]
    private void AddToCustomList()
    {
        var name = ModelName.Trim();
        if (name.Length == 0 || ContainsIgnoreCase(CustomModels, name))
        {
            return;
        }

        CustomModels.Add(name);
        _provider.CustomModels.Add(name);
        Persist();
        SyncModelOptions();
        ModelFetchMessage = $"已加入自建列表：{name}";
    }

    private bool CanAddToCustomList() =>
        ModelName.Trim().Length > 0 && !ContainsIgnoreCase(CustomModels, ModelName.Trim());

    /// <summary>删掉下拉里的某条候选（行尾的 ×）：自建的连落盘一起删，接口拉回的只删本次会话那份。</summary>
    [RelayCommand]
    private void RemoveModel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // 下拉项里的 × 会跟 ComboBoxItem 抢这次点击：期间不让选中项写回模型框
        _removingCustomModel = true;
        try
        {
            var shown = IndexOfIgnoreCase(CustomModels, name);
            if (shown >= 0)
            {
                CustomModels.RemoveAt(shown);
            }

            var stored = IndexOfIgnoreCase(_provider.CustomModels, name);
            if (stored >= 0)
            {
                _provider.CustomModels.RemoveAt(stored);
            }

            // 接口拉回的那份不落盘，删了只影响本次会话
            var fetched = IndexOfIgnoreCase(FetchedModels, name);
            if (fetched >= 0)
            {
                FetchedModels.RemoveAt(fetched);
            }

            Persist();
            SyncModelOptions();
            ModelFetchMessage = "";
        }
        finally
        {
            _removingCustomModel = false;
        }
    }

    /// <summary>
    /// 下拉里把一条自建模型拖到 <paramref name="targetName"/> 这一行的上缘或下缘（<paramref name="after"/> = 下缘）。
    /// 落点换算成自建列表的下标：向下拖就落在目标后面，向上拖就落在目标前面。
    /// </summary>
    internal void MoveCustomModelNear(string name, string targetName, bool after)
    {
        var from = IndexOfIgnoreCase(CustomModels, name);
        var to = IndexOfIgnoreCase(ModelOptions, targetName);
        if (from < 0 || to < 0)
        {
            return;   // 拖的不是自建条目，或落点已经不在列表里了
        }

        // 落到接口拉回的那一段上（to 超出自建条数）＝ 拖到自建列表末尾
        MoveCustomModelToIndex(name, LlmProvider.IndexForDropSlot(from, after ? to + 1 : to, CustomModels.Count));
    }

    /// <summary>把自建列表里的一条挪到第 <paramref name="index"/> 位，落盘顺序跟着一起改。</summary>
    internal void MoveCustomModelToIndex(string name, int index)
    {
        if (!_provider.MoveCustomModelToIndex(name, index))
        {
            return;
        }

        // 界面这两份用 Move 跟着挪，不重建：清空 ItemsSource 会让整个下拉项重建，弹层当场一闪
        var shown = IndexOfIgnoreCase(CustomModels, name);
        if (shown >= 0)
        {
            CustomModels.Move(shown, index);
        }

        // ModelOptions = 自建在前 + 接口拉回在后，自建条的序号两边一一对应
        var option = IndexOfIgnoreCase(ModelOptions, name);
        if (option >= 0)
        {
            ModelOptions.Move(option, index);
        }

        Persist();
    }

    /// <summary>清掉接口拉回的候选与提示；自建条目保留（那些要留着）。</summary>
    [RelayCommand(CanExecute = nameof(CanClearFetchedModels))]
    private void ClearFetchedModels()
    {
        FetchedModels.Clear();
        PickedModel = "";
        SyncModelOptions();
        ModelFetchMessage = "";
    }

    private bool CanClearFetchedModels() => FetchedModels.Count > 0;

    /// <summary>
    /// 把下拉项同步成「自建 + 接口拉回」。重建后把选中项恢复回去——
    /// 清空 ItemsSource 会让 ComboBox 把 SelectedItem 置空并写回 VM。
    /// </summary>
    private void SyncModelOptions()
    {
        var keep = PickedModel;
        ModelOptions.Clear();
        // 自建在前（这些在下拉里带删除 ×），接口拉回的在后
        foreach (var name in LlmProvider.MergeModels(CustomModels, FetchedModels))
        {
            ModelOptions.Add(name);
        }

        PickedModel = keep;
        HasModelOptions = ModelOptions.Count > 0;
        ClearFetchedModelsCommand.NotifyCanExecuteChanged();
        AddToCustomListCommand.NotifyCanExecuteChanged();
    }

    private static bool ContainsIgnoreCase(IEnumerable<string> source, string name) =>
        source.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    private static int IndexOfIgnoreCase(IList<string> source, string name)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (string.Equals(source[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(ChipLabel));
        if (_loading)
        {
            return;
        }

        _provider.Name = value ?? "";
        Persist();
    }

    partial void OnBaseUrlChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _provider.BaseUrl = (value ?? "").Trim();
        ModelFetchMessage = "";
        Persist();
    }

    partial void OnModelNameChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _provider.Model = (value ?? "").Trim();
        Persist();

        // 手填的名字一旦跟下拉里选中的那条对不上，就把下拉的选择清掉：
        // 否则会出现「框里是 v2.5、下拉里还挂着 v2.6」这种自相矛盾的画面。
        if (_pickedModel.Length > 0 &&
            !string.Equals(_pickedModel, _provider.Model, StringComparison.OrdinalIgnoreCase))
        {
            _pickedModel = "";
            OnPropertyChanged(nameof(PickedModel));
        }

        ModelFetchMessage = "";

        // 「加入列表」的亮/灰跟着模型名走：换了个名字要当场亮起来，不能等别处刷新
        AddToCustomListCommand.NotifyCanExecuteChanged();
    }

    partial void OnWireApiChanged(LlmWireApi value)
    {
        if (_loading)
        {
            return;
        }

        _provider.WireApi = value;
        Persist();
    }

    partial void OnRequiresAuthChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _provider.RequiresAuth = value;
        Persist();
    }

    partial void OnApiKeyChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _provider.ApiKeyEncrypted = SecretStore.Protect(value);
        Persist();
    }

    partial void OnExtraHeadersChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        _provider.ExtraHeadersJson = (value ?? "").Trim();
        Persist();
    }

    /// <summary>从「获取模型列表」的结果里选一个填入模型名。</summary>
    [ObservableProperty]
    private string _pickedModel = "";

    partial void OnPickedModelChanged(string value)
    {
        if (_loading || _removingCustomModel || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        ModelName = value;
    }

    private void Persist()
    {
        _save(_settings);
        _onChanged();
    }
}
