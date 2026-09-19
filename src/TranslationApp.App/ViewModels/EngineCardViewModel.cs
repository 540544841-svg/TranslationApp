using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>引擎配置字段的输入控件类型。</summary>
public enum EngineFieldKind
{
    /// <summary>明文标识（SecretId / AppId / Region 等）。</summary>
    Plain,

    /// <summary>密钥：经 DPAPI 加密落盘，界面用密码框 + 显示/隐藏切换。</summary>
    Secret,

    /// <summary>从固定候选中选择（如腾讯 Region）。</summary>
    Choice,
}

/// <summary>
/// 引擎卡片里的一个配置字段（13.1.6）：标题 + 说明 + 右对齐输入控件。
/// 取值直接读写 <see cref="AppSettings"/>（经构造函数注入的读写委托），
/// 因此「变更即保存」的既有约定不变，无需额外的应用按钮。
/// </summary>
public sealed partial class EngineFieldViewModel : ObservableObject
{
    private readonly Action<string> _write;
    private readonly Action<string>? _afterChange;

    internal EngineFieldViewModel(
        string label,
        string hint,
        EngineFieldKind kind,
        string value,
        Action<string> write,
        Action<string>? afterChange = null,
        IReadOnlyList<string>? choices = null,
        double width = 240)
    {
        Label = label;
        Hint = hint;
        Kind = kind;
        Choices = choices;
        FieldWidth = width;
        _write = write;
        _afterChange = afterChange;
        _value = value; // 直接赋字段：初始化不触发保存
    }

    public string Label { get; }

    public string Hint { get; }

    public EngineFieldKind Kind { get; }

    /// <summary>Choice 类型的候选项（已并入当前值，避免自定义值被下拉吞掉）。</summary>
    public IReadOnlyList<string>? Choices { get; }

    public double FieldWidth { get; }

    // 三种控件由 XAML 按类型显隐（避免为三种字段各写一个 DataTemplate）
    public bool IsPlain => Kind == EngineFieldKind.Plain;

    public bool IsSecret => Kind == EngineFieldKind.Secret;

    public bool IsChoice => Kind == EngineFieldKind.Choice;

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value)
    {
        _write(value ?? "");
        _afterChange?.Invoke(value ?? "");
    }
}

/// <summary>
/// 「引擎」页的一张引擎卡片（13.1.6）：引擎名 + 配置状态徽标 + 测试连接按钮 + 若干配置字段。
/// 配置状态直接取 <see cref="ITranslator.IsConfigured"/>（密钥改动后即时刷新），
/// 每次变更都通过回调通知宿主刷新引擎下拉与「当前引擎是否仍可用」。
/// </summary>
public sealed partial class EngineCardViewModel : ObservableObject
{
    /// <summary>「测试连接」固定测试文本（13.1.6：hello，en → zh-CN）。</summary>
    private const string TestText = "hello";
    private const string TestSource = "en";
    private const string TestTarget = "zh-CN";

    private readonly ITranslator _translator;
    private readonly Action _onChanged;
    private readonly Action<AppSettings> _save;
    private readonly AppSettings _settings;
    private readonly TranslationApp.Core.History.EngineStatsRepository? _stats;
    private bool _suppressEndpointWrite;

    /// <summary>初始化高级选项时不回写设置（与端点开关同理）。</summary>
    private bool _suppressAdvancedWrite;

    /// <summary>用户在本会话中手动切换过端点开关（此后不再按 Key 后缀自动同步）。</summary>
    private bool _endpointOverriddenByUser;

    internal EngineCardViewModel(
        ITranslator translator,
        string description,
        AppSettings settings,
        Action<AppSettings> save,
        Action onChanged,
        TranslationApp.Core.History.EngineStatsRepository? stats = null)
    {
        _translator = translator;
        _settings = settings;
        _save = save;
        _onChanged = onChanged;
        _stats = stats;

        Id = translator.Id;
        Name = translator.Name;
        Description = description;
        IsConfigured = translator.IsConfigured;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>卡片副标题（免费额度与配置说明）。</summary>
    public string Description { get; }

    public ObservableCollection<EngineFieldViewModel> Fields { get; } = [];

    /// <summary>主机名（可达性提示与「测试连接」失败文案里展示，例如 api-free.deepl.com）。</summary>
    public string Host { get; internal set; } = "";

    /// <summary>是否需要探测端点可达性（国外引擎：Azure / DeepL，见 13.9）。</summary>
    public bool NeedsReachabilityProbe { get; internal set; }

    /// <summary>可达性探测用的地址（无凭据请求）。</summary>
    internal Uri? ReachabilityUri { get; set; }

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private string _testMessage = "";

    [ObservableProperty]
    private bool _testSucceeded;

    [ObservableProperty]
    private bool _isTesting;

    /// <summary>可达性提示（仅在不可达时给出，可达时不打扰用户）。</summary>
    [ObservableProperty]
    private string _reachabilityWarning = "";

    /// <summary>P0 批 1 / spec §4.3：近 7 天成败看板文本（无数据为空并隐藏整行）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private string _statsText = "";

    /// <summary>是否显示看板行。</summary>
    public bool HasStats => StatsText.Length > 0;

    /// <summary>切到「引擎」页时刷新看板（统计由翻译链路的装饰器写入）。</summary>
    internal void RefreshStats()
    {
        var s = _stats?.GetSummary(Id) ?? new TranslationApp.Core.History.EngineStatsSummary(0, 0, 0, 0, 0, 0, "");
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
        if (s.LastError.Length > 0 && s.FailTotal > 0) parts.Add($"最近失败：{s.LastError}");

        StatsText = string.Join(" · ", parts);
    }

    /// <summary>是否显示端点不可达提示条。</summary>
    public bool HasReachabilityWarning => ReachabilityWarning.Length > 0;

    partial void OnReachabilityWarningChanged(string value) =>
        OnPropertyChanged(nameof(HasReachabilityWarning));

    /// <summary>是否显示「免费端点」开关（仅 DeepL）。</summary>
    public bool HasFreeEndpointToggle { get; internal set; }

    /// <summary>DeepL 免费端点开关（默认值由 Key 后缀自动判定，可人工覆盖）。</summary>
    [ObservableProperty]
    private bool _useFreeEndpoint;

    public string StatusText => IsConfigured ? "已配置" : "未配置";

    internal ITranslator Translator => _translator;

    partial void OnIsConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        TestCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseFreeEndpointChanged(bool value)
    {
        if (_suppressEndpointWrite)
        {
            return;
        }

        // 人工覆盖：此后不再按 Key 后缀自动同步（13.1.5）
        _endpointOverriddenByUser = true;
        Save(s => s.DeepLUseFreeEndpoint = value);
        TestMessage = "";
    }

    // ==================== AI 引擎的「高级选项」折叠区（13.3.2 / 13.5.1）====================

    /// <summary>是否显示「高级选项」折叠区（仅 AI 引擎）。</summary>
    public bool HasAdvancedOptions { get; internal set; }

    /// <summary>折叠区是否展开（默认收起，避免卡片过长）。</summary>
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

    /// <summary>启用并初始化高级选项（构造期不回写设置）。</summary>
    internal void EnableAdvancedOptions(string prompt, double temperature)
    {
        HasAdvancedOptions = true;
        _suppressAdvancedWrite = true;
        Prompt = prompt;
        Temperature = temperature;
        _suppressAdvancedWrite = false;
    }

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    /// <summary>恢复内置 Prompt（13.3.2「留空即用内置」）。</summary>
    [RelayCommand]
    private void ResetPrompt()
    {
        Prompt = "";
        AdvancedMessage = "已恢复内置 Prompt（只输出译文）";
    }

    partial void OnPromptChanged(string value)
    {
        if (_suppressAdvancedWrite)
        {
            return;
        }

        Save(s => s.LlmPrompt = value ?? "");
        TestMessage = "";
        AdvancedMessage = "";
    }

    partial void OnTemperatureChanged(double value)
    {
        if (_suppressAdvancedWrite)
        {
            return;
        }

        Save(s => s.LlmTemperature = value);
        TestMessage = "";
        AdvancedMessage = "";
    }

    /// <summary>字段变更后刷新配置状态并通知宿主（引擎下拉、当前引擎回退）。</summary>
    internal void HandleFieldChanged()
    {
        IsConfigured = _translator.IsConfigured;
        TestMessage = "";
        _onChanged();
    }

    /// <summary>
    /// DeepL 的 Key 变更（13.1.5）：先刷新配置状态，再按 Key 后缀同步端点开关；
    /// 用户在本会话中手动切换过开关则保留其选择（人工覆盖优先）。
    /// </summary>
    internal void HandleDeepLKeyChanged(string key)
    {
        HandleFieldChanged();
        if (_endpointOverriddenByUser)
        {
            return;
        }

        SyncFreeEndpointFromKey(key);
    }

    /// <summary>启用并初始化 DeepL 端点开关（构造期不回写设置）。</summary>
    internal void EnableFreeEndpointToggle(bool value)
    {
        HasFreeEndpointToggle = true;
        _suppressEndpointWrite = true;
        UseFreeEndpoint = value;
        _suppressEndpointWrite = false;
    }

    /// <summary>按 Key 后缀同步 DeepL 端点开关（13.1.5：默认自动判定）。</summary>
    private void SyncFreeEndpointFromKey(string key)
    {
        var free = DeepLTranslator.KeyUsesFreeEndpoint(key);
        _suppressEndpointWrite = true;
        UseFreeEndpoint = free;
        _suppressEndpointWrite = false;
        Save(s => s.DeepLUseFreeEndpoint = free);
    }

    internal void ApplyReachability(EndpointReachability status)
    {
        ReachabilityWarning = status == EndpointReachability.Unreachable
            ? $"端点 {Host} 当前不可达：请检查网络，或在本程序「高级」页开启代理后重试。"
            : "";
    }

    /// <summary>发一次真实翻译请求验证 Key（13.1.6）。</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        IsTesting = true;
        TestMessage = "";
        var stopwatch = Stopwatch.StartNew();

        // P0 批 1 / spec §4.2：「测试连接」不是真实翻译，整段抑制引擎看板计数（术语替换不受影响）
        using (GlossaryTranslator.SuppressStats())
        try
        {
            var result = await _translator.TranslateAsync(TestText, TestSource, TestTarget);
            stopwatch.Stop();

            var translated = result.TranslatedText.Trim().ReplaceLineEndings(" ");
            TestSucceeded = true;
            TestMessage = $"连接成功：译文「{translated}」，耗时 {stopwatch.Elapsed.TotalSeconds:0.0}s";
            Log.Information("测试连接成功：{Engine}，{Elapsed} ms", Name, stopwatch.ElapsedMilliseconds);
        }
        catch (TranslationException ex)
        {
            TestSucceeded = false;
            TestMessage = DescribeFailure(ex);
            // 只记录分类，不记录可能回显凭据的引擎原文
            Log.Warning("测试连接失败：{Engine}，错误类型 {ErrorType}", Name, ex.ErrorType);
        }
        catch (Exception ex)
        {
            TestSucceeded = false;
            TestMessage = "连接失败：发生未预期错误，详见日志";
            Log.Error(ex, "测试连接出现未预期异常（引擎={Engine}）", Name);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => IsConfigured;

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

    /// <summary>
    /// 展示前过滤明文密钥：部分服务会把凭据回显在错误消息里（13.1.6 安全要求）。
    /// </summary>
    private string Redact(string text)
    {
        foreach (var field in Fields)
        {
            var secret = field.Value;
            if (field.Kind == EngineFieldKind.Secret && secret.Length >= 6)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private void Save(Action<AppSettings> apply)
    {
        apply(_settings);
        _save(_settings);
    }
}
