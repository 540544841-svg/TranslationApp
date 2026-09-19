using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 引擎失败自动降级策略（FR-028 / 14.4）——纯逻辑、无 IO，因此判定矩阵与提示文案都能单测。
///
/// 三条不可动摇的原则（14.4.1 / 14.4.2）：
/// ① **只降一级**且固定降到 <see cref="AppSettings.FallbackEngineId"/>（默认 Bing 非官方接口）；
///    刻意**不采纳**「按已配置引擎顺序依次尝试」——官方引擎（腾讯/百度/Azure/DeepL/AI）消耗的是
///    用户自己的配额，在用户不知情时把请求打过去属于越权消耗用户资产。
/// ② 只对**引擎侧失败**降级（Network / QuotaExceeded / Engine）。InvalidKey 是用户配置问题
///    （降级会掩盖「Key 填错了」，让用户永远发现不了）；用户取消会抛 OperationCanceledException
///    而非 TranslationException，天然不会走到这里。
/// ③ **不修改 settings.Engine**：每次请求都先走用户选的引擎，降级只是本次请求的兜底
///    （静默改设置会造成「设置显示 Google、实际一直在用 Bing」的长期不一致）。
/// </summary>
public static class EngineFallback
{
    /// <summary>同一进程内累计降级达到该次数后，用托盘气泡一次性建议改默认引擎（14.4.2）。</summary>
    public const int SuggestThreshold = 3;

    /// <summary>
    /// 该错误分类是否属于「值得换一家引擎再试」（14.4.1 策略表）。
    /// <c>Engine</c> 一律触发是刻意的简化：当前分类无法区分 400/404 与 5xx，而两种情况下换一家都值得一试。
    /// </summary>
    public static bool IsFallbackError(TranslationErrorType errorType) => errorType switch
    {
        TranslationErrorType.Network => true,        // 不可达 / 超时：换引擎是唯一有效的自愈手段
        TranslationErrorType.QuotaExceeded => true,  // 429/403 限流：另一家不受此出口 IP 影响
        TranslationErrorType.Engine => true,         // 非 2xx 其它 / 响应结构异常：换一家成功率明显更高
        TranslationErrorType.InvalidKey => false,    // 用户配置问题，绝不掩盖
        _ => false,
    };

    /// <summary>
    /// 是否应当降级（判定矩阵的唯一入口）：
    /// 错误分类可降级 + 开关开启 + 备用引擎已配置 + 当前引擎不是备用引擎自己。
    /// </summary>
    /// <param name="primaryEngineId">主引擎 Id（当前 <see cref="AppSettings.Engine"/> 解析出的引擎）。</param>
    /// <param name="fallbackEngineId">备用引擎 Id；不存在时为 null。</param>
    /// <param name="fallbackConfigured">备用引擎的 <see cref="ITranslator.IsConfigured"/>。</param>
    public static bool ShouldFallback(
        TranslationErrorType errorType,
        bool enabled,
        string? primaryEngineId,
        string? fallbackEngineId,
        bool fallbackConfigured)
    {
        if (!enabled || !fallbackConfigured || !IsFallbackError(errorType))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(primaryEngineId) || string.IsNullOrWhiteSpace(fallbackEngineId))
        {
            return false;
        }

        // 边界 ①：当前引擎就是备用引擎 → 降到自己是死循环，直接不降（AC 4）
        return !string.Equals(primaryEngineId, fallbackEngineId, StringComparison.Ordinal);
    }

    // ==================== 用户知情文案（位置与措辞按 14.4.2，全部显示在小窗状态行） ====================

    /// <summary>降级进行中（等待期间先显示，不能只有干等）。</summary>
    public static string InProgressStatus(string primaryName, string fallbackName) =>
        $"{primaryName} 不可用，正在改用 {fallbackName} 翻译…";

    /// <summary>降级成功（带着错误摘要，让用户知道发生了什么）。</summary>
    public static string SuccessStatus(string primaryName, string fallbackName, string? errorSummary) =>
        string.IsNullOrWhiteSpace(errorSummary)
            ? $"{primaryName} 不可用，已自动改用 {fallbackName} 翻译"
            : $"{primaryName} 不可用（{errorSummary}），已自动改用 {fallbackName} 翻译";

    /// <summary>降级也失败（错误条只保留主引擎的分类文案，这里是补充说明）。</summary>
    public static string BothFailedStatus(string fallbackName) =>
        $"已尝试备用引擎 {fallbackName}，同样失败";

    /// <summary>累计降级达阈值时的一次性托盘气泡：只**建议**改设置，不代改。</summary>
    public static string SuggestionBalloon(string primaryName, string fallbackName) =>
        $"{primaryName} 已连续多次不可用，可在「设置 → 翻译」中把默认引擎改为 {fallbackName}";
}

/// <summary>
/// 同一进程内的累计降级计数（FR-028 / 14.4.2）：达到 <see cref="EngineFallback.SuggestThreshold"/> 次时
/// 用托盘气泡建议改默认引擎，每次进程**只提示一次**（不新增可点击控件，也不自动改设置）。
/// </summary>
public sealed class EngineFallbackCounter
{
    /// <summary>进程内累计降级成功次数。</summary>
    public int Count { get; private set; }

    /// <summary>是否已经弹过建议气泡（保证「同一进程内仅一次」）。</summary>
    public bool Suggested { get; private set; }

    /// <summary>记录一次降级；返回 true 表示本次应当弹一次性提示气泡。</summary>
    public bool Record()
    {
        Count++;
        if (Suggested || Count < EngineFallback.SuggestThreshold)
        {
            return false;
        }

        Suggested = true;
        return true;
    }
}
