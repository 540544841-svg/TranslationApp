using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 引擎失败自动降级（FR-028 / 14.4.1 策略表、14.4.2 文案、AC 1~9）。
/// 全部判定都在 Core 的纯函数里，因此「哪些错误触发降级、只降一级、两条边界不降级、
/// 一次性托盘提示的触发条件」都能在不联网的情况下穷举——这是本批的主要验收依据。
/// </summary>
public sealed class EngineFallbackTests
{
    private const string Primary = "google";
    private const string Fallback = "bing";

    private static bool ShouldFallback(TranslationErrorType errorType, bool enabled = true) =>
        EngineFallback.ShouldFallback(errorType, enabled, Primary, Fallback, fallbackConfigured: true);

    // ---------------- 策略表：哪些错误触发降级（14.4.1） ----------------

    [Theory]
    [InlineData(TranslationErrorType.Network)]        // 不可达 / 超时：换引擎是唯一有效的自愈手段
    [InlineData(TranslationErrorType.QuotaExceeded)]  // 429 / 403 限流：另一家不受该出口 IP 影响
    [InlineData(TranslationErrorType.Engine)]         // 非 2xx 其它 / 响应格式异常
    public void ShouldFallback_引擎侧失败_触发降级(TranslationErrorType errorType) =>
        Assert.True(ShouldFallback(errorType));

    [Fact]
    public void ShouldFallback_InvalidKey_不触发降级()
    {
        // Key 无效是**用户配置问题**：降级会掩盖它，让用户永远发现不了 Key 填错了（14.4.1）
        Assert.False(ShouldFallback(TranslationErrorType.InvalidKey));
    }

    [Theory]
    [InlineData(TranslationErrorType.Network)]
    [InlineData(TranslationErrorType.QuotaExceeded)]
    [InlineData(TranslationErrorType.Engine)]
    [InlineData(TranslationErrorType.InvalidKey)]
    public void ShouldFallback_开关关闭_任何错误都不降级(TranslationErrorType errorType)
    {
        // AC 2：EnableEngineFallback = false 时直接给出错误条，不发生第二次请求
        Assert.False(ShouldFallback(errorType, enabled: false));
    }

    // ---------------- 只降一级 / 不重复降级的边界（14.4.1 两个边界） ----------------

    [Fact]
    public void ShouldFallback_当前引擎就是备用引擎_不降级()
    {
        // AC 4：降到自己是死循环；当前引擎为 Bing、Bing 不可用时直接报错
        Assert.False(EngineFallback.ShouldFallback(
            TranslationErrorType.Network, enabled: true, "bing", "bing", fallbackConfigured: true));
    }

    [Fact]
    public void ShouldFallback_备用引擎未配置_不降级()
    {
        // AC 5：指向未配置 Key 的官方引擎时不降级（不产生无意义的请求，也不动用用户配额）
        Assert.False(EngineFallback.ShouldFallback(
            TranslationErrorType.Network, enabled: true, Primary, "azure", fallbackConfigured: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonexistent")]
    public void ShouldFallback_备用引擎不存在_不降级(string? fallbackId)
    {
        // TranslatorCatalog.Find 对未知 Id 返回 null，因此这里也必须是「不降级」而不是回落到别的引擎
        Assert.False(EngineFallback.ShouldFallback(
            TranslationErrorType.Network, enabled: true, Primary, fallbackId, fallbackConfigured: false));
    }

    // ---------------- 累计计数与一次性托盘气泡（14.4.2 / AC 9） ----------------

    [Fact]
    public void Counter_累计三次才提示_且每次进程仅提示一次()
    {
        var counter = new EngineFallbackCounter();

        Assert.False(counter.Record()); // 第 1 次
        Assert.False(counter.Record()); // 第 2 次
        Assert.True(counter.Record());  // 第 3 次：达阈值 → 弹气泡
        Assert.False(counter.Record()); // 第 4 次：已提示过，不再弹
        Assert.False(counter.Record());

        Assert.Equal(5, counter.Count);
        Assert.True(counter.Suggested);
    }

    [Fact]
    public void Counter_初始状态为未降级未提示()
    {
        var counter = new EngineFallbackCounter();

        Assert.Equal(0, counter.Count);
        Assert.False(counter.Suggested);
        Assert.Equal(3, EngineFallback.SuggestThreshold);
    }

    // ---------------- 用户知情文案（14.4.2，位置与措辞按文档） ----------------

    [Fact]
    public void InProgressStatus_文案含两个引擎名与省略号()
    {
        var text = EngineFallback.InProgressStatus("Google（非官方）", "Bing（非官方）");

        Assert.Equal("Google（非官方） 不可用，正在改用 Bing（非官方） 翻译…", text);
    }

    [Fact]
    public void SuccessStatus_文案含错误摘要()
    {
        var text = EngineFallback.SuccessStatus("Google（非官方）", "Bing（非官方）", "请求超时");

        Assert.Equal("Google（非官方） 不可用（请求超时），已自动改用 Bing（非官方） 翻译", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SuccessStatus_无错误摘要时不留空括号(string? summary)
    {
        var text = EngineFallback.SuccessStatus("Google（非官方）", "Bing（非官方）", summary);

        Assert.Equal("Google（非官方） 不可用，已自动改用 Bing（非官方） 翻译", text);
    }

    [Fact]
    public void BothFailedStatus_说明已试过备用引擎()
    {
        Assert.Equal("已尝试备用引擎 Bing（非官方），同样失败", EngineFallback.BothFailedStatus("Bing（非官方）"));
    }

    [Fact]
    public void SuggestionBalloon_指向设置页的翻译分区()
    {
        var text = EngineFallback.SuggestionBalloon("Google（非官方）", "Bing（非官方）");

        Assert.Contains("设置 → 翻译", text);
        Assert.Contains("Google（非官方）", text);
        Assert.Contains("Bing（非官方）", text);
    }
}
