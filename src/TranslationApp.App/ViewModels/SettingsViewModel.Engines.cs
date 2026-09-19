using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>
/// 设置窗口「引擎」页（FR-024 / 13.1.6）与「翻译」页引擎下拉的「（未配置）」状态。
/// 单独拆一个 partial 文件，避免 SettingsViewModel 主文件继续膨胀。
/// 归属见 13.5：引擎选择留在「翻译」页（不重复出现），本页只承载各引擎的 Key 与测试连接。
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>腾讯云 TMT 常用 Region（13.9 第 2 项：取值以控制台为准，非列表值可手改配置文件）。</summary>
    private static readonly string[] TencentRegions =
    [
        "ap-guangzhou",
        "ap-shanghai",
        "ap-beijing",
        "ap-hongkong",
        "ap-singapore",
    ];

    /// <summary>「引擎」页的引擎卡片（腾讯 / 百度 / Azure / DeepL）。</summary>
    public ObservableCollection<EngineCardViewModel> EngineCards { get; } = [];

    /// <summary>「翻译」页引擎下拉项（未配置的引擎带「（未配置）」后缀且不可选）。</summary>
    public ObservableCollection<EngineOptionViewModel> EngineOptions { get; } = [];

    /// <summary>按引擎 Id 索引下拉项，密钥变更时就地刷新状态（重建集合会清空下拉的当前选择）。</summary>
    private readonly Dictionary<string, EngineOptionViewModel> _engineOptionsById = new(StringComparer.Ordinal);

    /// <summary>「翻译」页的引擎提示（未配置回退、或选到未配置引擎时的原因）。</summary>
    [ObservableProperty]
    private string _engineMessage = "";

    /// <summary>构建「引擎」页并初始化引擎下拉（在构造函数中调用，_catalog 已就绪）。</summary>
    private void InitializeEnginePage()
    {
        foreach (var translator in _catalog.All)
        {
            var card = CreateEngineCard(translator);
            if (card is not null)
            {
                EngineCards.Add(card);
            }
        }

        RefreshEngineOptions();
        EnsureCurrentEngineConfigured();
        InitializeCompareOptions();
        InitializeFallbackOptions();
        ProbeForeignEndpoints();
    }

    /// <summary>
    /// 按引擎 Id 组装卡片。Bing/Google 无需 Key，不在此页。
    /// 返回 null 表示该引擎无需配置。
    /// </summary>
    private EngineCardViewModel? CreateEngineCard(ITranslator translator) => translator.Id switch
    {
        "tencent" => CreateTencentCard(translator),
        "baidu" => CreateBaiduCard(translator),
        "azure" => CreateAzureCard(translator),
        "deepl" => CreateDeepLCard(translator),
        "llm" => CreateLlmCard(translator),
        _ => null,
    };

    private EngineCardViewModel CreateTencentCard(ITranslator translator)
    {
        var card = NewCard(
            translator,
            "机器翻译 TMT，每月 500 万字符免费额度，需实名认证（政策以控制台用量页为准）。");

        // Region 为空时回填列表首项，保证下拉显示值与落盘值一致
        if (string.IsNullOrWhiteSpace(_settings.TencentRegion))
        {
            Save(s => s.TencentRegion = TencentRegions[0]);
        }

        AddField(card, new EngineFieldViewModel(
            "SecretId", "腾讯云 CAM → API 密钥管理",
            EngineFieldKind.Plain, _settings.TencentSecretId,
            v => Save(s => s.TencentSecretId = v),
            _ => card.HandleFieldChanged()));

        AddField(card, new EngineFieldViewModel(
            "SecretKey", "经 DPAPI 加密后落盘，配置文件中无明文",
            EngineFieldKind.Secret, SecretStore.Unprotect(_settings.TencentSecretKeyEncrypted) ?? "",
            v => Save(s => s.TencentSecretKeyEncrypted = SecretStore.Protect(v)),
            _ => card.HandleFieldChanged()));

        AddField(card, new EngineFieldViewModel(
            "区域", "X-TC-Region；请与腾讯云控制台开通的区域一致",
            EngineFieldKind.Choice, _settings.TencentRegion,
            v => Save(s => s.TencentRegion = v),
            _ => card.HandleFieldChanged(),
            choices: TencentRegions,
            width: 160));

        return card;
    }

    private EngineCardViewModel CreateBaiduCard(ITranslator translator)
    {
        var card = NewCard(
            translator,
            "通用文本翻译标准版，未认证 5 万字符/月，个人认证 100 万字符/月。");

        AddField(card, new EngineFieldViewModel(
            "APPID", "百度翻译开放平台 → 管理控制台 → 我的应用",
            EngineFieldKind.Plain, _settings.BaiduAppId,
            v => Save(s => s.BaiduAppId = v),
            _ => card.HandleFieldChanged()));

        AddField(card, new EngineFieldViewModel(
            "密钥", "经 DPAPI 加密后落盘，配置文件中无明文",
            EngineFieldKind.Secret, SecretStore.Unprotect(_settings.BaiduAppKeyEncrypted) ?? "",
            v => Save(s => s.BaiduAppKeyEncrypted = SecretStore.Protect(v)),
            _ => card.HandleFieldChanged()));

        return card;
    }

    private EngineCardViewModel CreateAzureCard(ITranslator translator)
    {
        var card = NewCard(
            translator,
            "翻译工具 V3，免费层 F0 = 200 万字符/月；申请需信用卡（四家中门槛最高）。");
        MarkForeignEndpoint(card, AzureTranslator.Endpoint);

        AddField(card, new EngineFieldViewModel(
            "订阅密钥", "Azure 门户 → Translator 资源 → 密钥和终结点",
            EngineFieldKind.Secret, SecretStore.Unprotect(_settings.AzureSubscriptionKeyEncrypted) ?? "",
            v => Save(s => s.AzureSubscriptionKeyEncrypted = SecretStore.Protect(v)),
            _ => card.HandleFieldChanged()));

        AddField(card, new EngineFieldViewModel(
            "区域", "如 eastasia；仅「多服务资源」需要，单服务资源可留空",
            EngineFieldKind.Plain, _settings.AzureRegion,
            v => Save(s => s.AzureRegion = v.Trim()),
            _ => card.HandleFieldChanged(),
            width: 200));

        return card;
    }

    private EngineCardViewModel CreateDeepLCard(ITranslator translator)
    {
        var card = NewCard(
            translator,
            "API Free = 50 万字符/月，无需信用卡；Key 以 :fx 结尾为免费版。");
        MarkForeignEndpoint(card, DeepLTranslator.FreeEndpointUrl);
        card.EnableFreeEndpointToggle(_settings.DeepLUseFreeEndpoint);

        AddField(card, new EngineFieldViewModel(
            "Authentication Key", "经 DPAPI 加密后落盘，配置文件中无明文",
            EngineFieldKind.Secret, SecretStore.Unprotect(_settings.DeepLApiKeyEncrypted) ?? "",
            v => Save(s => s.DeepLApiKeyEncrypted = SecretStore.Protect(v)),
            card.HandleDeepLKeyChanged,
            width: 260));

        return card;
    }

    /// <summary>
    /// AI 引擎卡片（FR-022 / 13.3.1）：接口地址 + API Key + 模型名，末尾折叠区放温度与自定义 Prompt。
    /// 不探测端点可达性：默认的 DeepSeek 在国内可直连，是否走代理由「高级」页的作用范围决定。
    /// </summary>
    private EngineCardViewModel CreateLlmCard(ITranslator translator)
    {
        var card = NewCard(
            translator,
            $"OpenAI 兼容接口（DeepSeek / OpenAI / Ollama 等），按字符计费。默认 {LlmTranslator.DefaultBaseUrl}"
            + $" + {LlmTranslator.DefaultModel}；三项填齐后到「翻译」页选择本引擎。");

        AddField(card, new EngineFieldViewModel(
            "接口地址", "填根地址或 /v1 均可，程序按 /v1/chat/completions 自动补全",
            EngineFieldKind.Plain, _settings.LlmBaseUrl,
            v => Save(s => s.LlmBaseUrl = v.Trim()),
            _ => card.HandleFieldChanged(),
            width: 300));

        AddField(card, new EngineFieldViewModel(
            "API Key", "经 DPAPI 加密后落盘，配置文件中无明文",
            EngineFieldKind.Secret, SecretStore.Unprotect(_settings.LlmApiKeyEncrypted) ?? "",
            v => Save(s => s.LlmApiKeyEncrypted = SecretStore.Protect(v)),
            _ => card.HandleFieldChanged(),
            width: 300));

        AddField(card, new EngineFieldViewModel(
            "模型名", "如 deepseek-chat / gpt-4o-mini；Ollama 填本地模型名",
            EngineFieldKind.Plain, _settings.LlmModel,
            v => Save(s => s.LlmModel = v.Trim()),
            _ => card.HandleFieldChanged(),
            width: 200));

        card.EnableAdvancedOptions(_settings.LlmPrompt, _settings.LlmTemperature);
        return card;
    }

    // ==================== FR-020 结果对比（13.4.1 / 13.5.1「翻译」页）====================

    /// <summary>「翻译」页「结果对比」卡片的引擎勾选项（全部引擎，未配置的置灰）。</summary>
    public ObservableCollection<CompareEngineOptionViewModel> CompareEngineOptions { get; } = [];

    /// <summary>对比设置的提示文案（勾选数不足、勾了未配置引擎等）。</summary>
    [ObservableProperty]
    private string _compareMessage = "";

    /// <summary>提示是否需要引起注意（用警告色）。</summary>
    [ObservableProperty]
    private bool _compareMessageIsWarning;

    /// <summary>对比结果是否写入历史（默认写入）。</summary>
    [ObservableProperty]
    private bool _compareIncludeInHistory;

    // ==================== FR-028 引擎失败自动降级（「翻译」页「当前引擎」卡片内两行） ====================

    /// <summary>引擎失败时自动改用备用引擎（默认开启）。只降一级、只作用于单引擎翻译路径。</summary>
    [ObservableProperty]
    private bool _enableEngineFallback = true;

    /// <summary>备用引擎 Id（默认 bing：零配置、国内可直连）。下拉项复用引擎下拉集合（未配置项不可选）。</summary>
    [ObservableProperty]
    private string _selectedFallbackEngine = "bing";

    partial void OnEnableEngineFallbackChanged(bool value)
    {
        if (!_fallbackInitialized)
        {
            return;
        }

        Save(s => s.EnableEngineFallback = value);
        UpdateFallbackMessage();
    }

    partial void OnSelectedFallbackEngineChanged(string value)
    {
        if (!_fallbackInitialized)
        {
            return;
        }

        if (!string.IsNullOrEmpty(value) && _settings.FallbackEngineId != value)
        {
            Save(s => s.FallbackEngineId = value);
            UpdateFallbackMessage();
        }
    }

    /// <summary>降级设置的一句话说明（备用引擎未配置时明确告知「不会降级」）。</summary>
    [ObservableProperty]
    private string _fallbackMessage = "";

    /// <summary>初始化完成前不落盘（构造期赋初值不应视为用户变更）。</summary>
    private bool _fallbackInitialized;

    /// <summary>构造期回填（不落盘）：备用引擎取设置里的原值，非法值不在此处自动纠正，以免掩盖 AC 5 的场景。</summary>
    private void InitializeFallbackOptions()
    {
        EnableEngineFallback = _settings.EnableEngineFallback;
        SelectedFallbackEngine = _settings.FallbackEngineId;
        _fallbackInitialized = true;
        UpdateFallbackMessage();
    }

    private void UpdateFallbackMessage()
    {
        var fallback = _catalog.Find(_settings.FallbackEngineId);
        if (!_settings.EnableEngineFallback)
        {
            FallbackMessage = "已关闭：引擎失败时直接给出错误提示，不发起第二次请求";
            return;
        }

        FallbackMessage = fallback is null
            ? $"备用引擎「{_settings.FallbackEngineId}」不存在，当前不会降级"
            : !fallback.IsConfigured
                ? $"备用引擎「{fallback.Name}」尚未配置 Key，当前不会降级"
                : $"首选引擎不可用时，本次请求自动改用 {fallback.Name}（不改动「当前引擎」；API Key 无效时不降级）";
    }
    /// <summary>上一次被接受的勾选集合（超出上限时整体回滚到它）。</summary>
    private HashSet<string> _compareSelectionSnapshot = new(StringComparer.Ordinal);

    private bool _suppressCompareWrite;

    /// <summary>初始化完成前不落盘（构造期赋初值不应视为用户变更）。</summary>
    private bool _compareInitialized;

    private void InitializeCompareOptions()
    {
        var selected = EngineComparison.ParseIds(_settings.CompareEngineIds);
        foreach (var translator in _catalog.All)
        {
            CompareEngineOptions.Add(new CompareEngineOptionViewModel(
                translator.Id, translator.Name, translator.IsConfigured,
                selected.Contains(translator.Id), OnCompareSelectionChanged));
        }

        _compareSelectionSnapshot = CurrentCompareSelection();
        CompareIncludeInHistory = _settings.CompareIncludeInHistory;
        _compareInitialized = true;
        UpdateCompareMessage();
    }

    /// <summary>密钥变更后就地刷新勾选项的可用性（不重建集合，避免勾选态丢失）。</summary>
    private void RefreshCompareOptions()
    {
        foreach (var option in CompareEngineOptions)
        {
            var translator = _catalog.All.FirstOrDefault(e => e.Id == option.Id);
            if (translator is not null)
            {
                option.UpdateAvailability(translator.IsConfigured);
            }
        }

        UpdateCompareMessage();
    }

    partial void OnCompareIncludeInHistoryChanged(bool value)
    {
        if (!_compareInitialized)
        {
            return;
        }

        Save(s => s.CompareIncludeInHistory = value);
    }

    private void OnCompareSelectionChanged()
    {
        if (_suppressCompareWrite)
        {
            return;
        }

        var current = CurrentCompareSelection();
        if (current.Count > EngineComparison.MaxEngines)
        {
            // 超出上限：整体回滚到上一次选择（比只撤销「最后点的那一个」更符合直觉）
            _suppressCompareWrite = true;
            foreach (var option in CompareEngineOptions)
            {
                option.IsSelected = _compareSelectionSnapshot.Contains(option.Id);
            }

            _suppressCompareWrite = false;
            CompareMessage = $"最多只能对比 {EngineComparison.MaxEngines} 个引擎，已撤销本次勾选";
            CompareMessageIsWarning = true;
            return;
        }

        _compareSelectionSnapshot = current;
        // 按引擎目录顺序落库，保证对比栏顺序稳定且可复现
        Save(s => s.CompareEngineIds = string.Join(
            ",",
            _catalog.All.Where(e => current.Contains(e.Id)).Select(e => e.Id)));
        UpdateCompareMessage();
    }

    private HashSet<string> CurrentCompareSelection() =>
        CompareEngineOptions.Where(o => o.IsSelected).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

    private void UpdateCompareMessage()
    {
        var configured = CompareEngineOptions.Where(o => o.IsSelected && o.IsEnabled).ToArray();
        var missing = CompareEngineOptions.Where(o => o.IsSelected && !o.IsEnabled).ToArray();

        if (configured.Length == 0)
        {
            CompareMessage = "未勾选：小窗点「对比」时将自动使用「当前引擎 + 第一个其它已配置引擎」";
            CompareMessageIsWarning = false;
            return;
        }

        if (configured.Length < EngineComparison.MinEngines)
        {
            CompareMessage = "还需再勾选 1 个已配置引擎，「对比」按钮才会启用";
            CompareMessageIsWarning = true;
            return;
        }

        CompareMessage = $"已勾选 {configured.Length} 个引擎";
        if (missing.Length > 0)
        {
            CompareMessage += $"；{string.Join("、", missing.Select(o => o.Display))} 尚未配置，不会参与对比";
        }

        CompareMessageIsWarning = missing.Length > 0;
    }

    /// <summary>新建卡片并接好「变更即刷新」的回调。</summary>
    private EngineCardViewModel NewCard(ITranslator translator, string description) =>
        new(translator, description, _settings, _store.Save, OnEngineCardChanged, _engineStats);

    private static void AddField(EngineCardViewModel card, EngineFieldViewModel field) =>
        card.Fields.Add(field);

    private static void MarkForeignEndpoint(EngineCardViewModel card, string url)
    {
        card.NeedsReachabilityProbe = true;
        card.ReachabilityUri = new Uri(url);
        card.Host = card.ReachabilityUri.Host;
    }

    /// <summary>任一密钥/标识字段变更后：刷新引擎下拉可用性，并保证当前引擎仍然可用。</summary>
    private void OnEngineCardChanged()
    {
        RefreshEngineOptions();
        EnsureCurrentEngineConfigured();
        RefreshCompareOptions();
    }

    /// <summary>
    /// 刷新引擎下拉项：未配置的引擎显示「（未配置）」且不可选（13.1.6）。
    /// 就地更新已有项而不重建集合 —— 清空 ItemsSource 会让 ComboBox 把 SelectedValue 置空并写回 VM。
    /// </summary>
    private void RefreshEngineOptions()
    {
        foreach (var translator in _catalog.All)
        {
            var configured = translator.IsConfigured;
            var display = configured ? translator.Name : $"{translator.Name}（未配置）";

            if (_engineOptionsById.TryGetValue(translator.Id, out var option))
            {
                option.Display = display;
                option.IsEnabled = configured;
                continue;
            }

            option = new EngineOptionViewModel(translator.Id, display, configured);
            _engineOptionsById[translator.Id] = option;
            EngineOptions.Add(option);
        }

        UpdateFallbackMessage(); // 密钥变更会影响「备用引擎是否已配置」的提示
    }

    /// <summary>
    /// 当前引擎若未配置（例如 Key 被清空）则自动回退到首个可用引擎并提示原因（13.1.6 / AC 2）。
    /// </summary>
    private void EnsureCurrentEngineConfigured()
    {
        var stored = _settings.Engine;
        var resolved = _catalog.Resolve(stored);
        if (string.Equals(resolved.Id, stored, StringComparison.Ordinal))
        {
            return;
        }

        var storedName = _catalog.All.FirstOrDefault(e => e.Id == stored)?.Name ?? stored;
        Save(s => s.Engine = resolved.Id);
        SelectedEngine = resolved.Id;
        EngineMessage = string.IsNullOrEmpty(stored)
            ? $"已自动选择可用的引擎：{resolved.Name}"
            : $"「{storedName}」尚未配置或密钥已失效，已自动切换到 {resolved.Name}";
        Log.Information("当前引擎不可用，已回退：{From} → {To}", stored, resolved.Id);
    }

    /// <summary>
    /// 探测国外引擎端点可达性（13.9）：仅在不可达时给出提示，不阻塞界面。
    /// 探测走引擎自己的代理作用范围，避免「开了代理却报不可达」的误判。
    /// </summary>
    private void ProbeForeignEndpoints()
    {
        var targets = EngineCards
            .Where(card => card.NeedsReachabilityProbe && card.ReachabilityUri is not null)
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var card in targets)
            {
                try
                {
                    var client = _httpProvider.Get(ProxyScope.GoogleOnly);
                    var status = await EngineReachability.ProbeAsync(
                        client, card.ReachabilityUri!, """{"text":["hello"],"target_lang":"ZH"}""");

                    if (status == EndpointReachability.Unreachable)
                    {
                        RunOnUiThread(() => card.ApplyReachability(status));
                        Log.Warning("端点不可达：{Host}（{Engine}）", card.Host, card.Name);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "端点可达性探测异常：{Host}", card.Host);
                }
            }
        });
    }

    /// <summary>后台探测完成后回到 UI 线程更新绑定属性。</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}

/// <summary>
/// 「翻译」页引擎下拉项：未配置的引擎带「（未配置）」后缀且不可选。
/// 属性可写，便于密钥变更时就地刷新（避免重建集合导致下拉丢失当前选择）。
/// </summary>
public sealed partial class EngineOptionViewModel : ObservableObject
{
    public EngineOptionViewModel(string id, string display, bool isEnabled)
    {
        Id = id;
        _display = display;
        _isEnabled = isEnabled;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _display;

    [ObservableProperty]
    private bool _isEnabled;
}

/// <summary>
/// 「翻译」页「结果对比」卡片里的一个引擎勾选项（13.4.1）。
/// IsEnabled 可写：密钥被清空后即时置灰（不重建集合，勾选态得以保留）。
/// </summary>
public sealed partial class CompareEngineOptionViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    internal CompareEngineOptionViewModel(
        string id, string display, bool isEnabled, bool isSelected, Action onSelectionChanged)
    {
        Id = id;
        Display = display;
        _isEnabled = isEnabled;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public string Id { get; }

    public string Display { get; }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    internal void UpdateAvailability(bool isConfigured) => IsEnabled = isConfigured;
}
