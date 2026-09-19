namespace TranslationApp.Core.Translation;

/// <summary>
/// 单个引擎的对比结果（13.4.3）：成功带译文，失败带错误分类，取消则两者皆无（UI 不展示为错误）。
/// </summary>
public sealed record EngineComparisonOutcome(
    string EngineId,
    string EngineName,
    string? Text,
    TranslationErrorType? ErrorType,
    bool IsCancelled)
{
    /// <summary>是否有译文。</summary>
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>是否失败（取消不算失败）。</summary>
    public bool IsFailure => !IsCancelled && ErrorType is not null;
}

/// <summary>
/// 引擎结果对比（FR-020 / 13.4）的纯逻辑部分：对比引擎集合的解析与并发聚合。
/// 放在 Core 而非 ViewModel，是为了让「空值默认规则、去重、过滤未配置、部分失败」这些易错点可单测。
/// 不持有 UI 状态：每完成一个引擎就回调一次（13.4.1「先到先显示」），由调用方决定如何呈现。
/// </summary>
public static class EngineComparison
{
    /// <summary>对比引擎数量上限（13.4.1：勾选 2~3 个）。</summary>
    public const int MaxEngines = 3;

    /// <summary>建议的对比引擎数量下限（低于此值「对比」按钮禁用）。</summary>
    public const int MinEngines = 2;

    /// <summary>
    /// 解析对比引擎集合（13.4.1）：
    /// 逗号分隔的 Id 列表 → 去空白、去重、保序、只保留已配置引擎、最多 3 个；
    /// 列表为空（或勾选的引擎全部未配置）→ 自动取「当前引擎 + 首个其它已配置引擎」。
    /// 显式只勾选了 1 个时不做补齐（保持 1 个，由调用方据此禁用「对比」按钮并提示）。
    /// </summary>
    public static IReadOnlyList<ITranslator> ResolveEngines(
        string? configuredIds, IReadOnlyList<ITranslator> all, string? currentEngineId)
    {
        var selected = ParseIds(configuredIds)
            .Select(id => all.FirstOrDefault(engine => engine.Id == id && engine.IsConfigured))
            .Where(engine => engine is not null)
            .Select(engine => engine!)
            .Take(MaxEngines)
            .ToArray();

        if (selected.Length > 0)
        {
            return selected;
        }

        var auto = new List<ITranslator>();
        var current = all.FirstOrDefault(engine => engine.Id == currentEngineId && engine.IsConfigured);
        if (current is not null)
        {
            auto.Add(current);
        }

        foreach (var engine in all.Where(engine => engine.IsConfigured && !ReferenceEquals(engine, current)))
        {
            if (auto.Count >= MinEngines)
            {
                break;
            }

            auto.Add(engine);
        }

        return auto;
    }

    /// <summary>对比引擎集合的稳定签名（用于判断「勾选集合是否变更」）。</summary>
    public static string Signature(IEnumerable<ITranslator> engines) =>
        string.Join(",", engines.Select(engine => engine.Id));

    /// <summary>解析逗号分隔的引擎 Id 列表：去空白、去重、保序、忽略空项（设置页也要用）。</summary>
    public static IReadOnlyList<string> ParseIds(string? configuredIds)
    {
        if (string.IsNullOrWhiteSpace(configuredIds))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var part in configuredIds.Split(','))
        {
            var id = part.Trim();
            if (id.Length == 0 || ids.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// 并发请求各引擎（13.4.1）。每个引擎独立 try/catch：一个失败不影响其他（13.4.3），
    /// 因此本方法不抛异常；取消时该引擎的 outcome 标记为 IsCancelled。
    /// <paramref name="onCompleted"/> 的 index 与 <paramref name="engines"/> 下标一一对应；
    /// 从 UI 线程调用时，await 续体会回到 UI 线程，回调可安全更新绑定属性；
    /// 回调在调用方的续体线程上触发（调用方无 UI 上下文时即线程池线程），
    /// 且各引擎的回调可能并发发生——回调内写共享状态须自行加锁或改用线程安全容器。
    /// </summary>
    public static async Task RunAsync(
        IReadOnlyList<ITranslator> engines,
        string text,
        string sourceLanguage,
        string targetLanguage,
        Action<int, EngineComparisonOutcome>? onCompleted,
        CancellationToken cancellationToken = default)
    {
        var tasks = new Task[engines.Count];
        for (var index = 0; index < engines.Count; index++)
        {
            tasks[index] = RunOneAsync(
                engines[index], index, text, sourceLanguage, targetLanguage, onCompleted, cancellationToken);
        }

        await Task.WhenAll(tasks);
    }

    private static async Task RunOneAsync(
        ITranslator engine,
        int index,
        string text,
        string sourceLanguage,
        string targetLanguage,
        Action<int, EngineComparisonOutcome>? onCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
            onCompleted?.Invoke(index, new EngineComparisonOutcome(
                engine.Id, engine.Name, result.TranslatedText, null, IsCancelled: false));
        }
        catch (OperationCanceledException)
        {
            // 关窗 / 退出对比：不算失败，也不产生错误文案（13.4.3）
            onCompleted?.Invoke(index, new EngineComparisonOutcome(
                engine.Id, engine.Name, null, null, IsCancelled: true));
        }
        catch (TranslationException ex)
        {
            onCompleted?.Invoke(index, new EngineComparisonOutcome(
                engine.Id, engine.Name, null, ex.ErrorType, IsCancelled: false));
        }
        catch (Exception)
        {
            // 未预期异常同样只影响该栏（不把整个对比带崩）
            onCompleted?.Invoke(index, new EngineComparisonOutcome(
                engine.Id, engine.Name, null, TranslationErrorType.Engine, IsCancelled: false));
        }
    }
}
