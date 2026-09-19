using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 引擎结果对比的纯逻辑测试（FR-020 / 13.4）：对比引擎集合的解析（空值默认规则、去重、
/// 过滤未配置、上限、顺序）与并发聚合的部分失败 / 取消语义。用假引擎，不发起真实请求。
/// </summary>
public class EngineComparisonTests
{
    /// <summary>可控的假引擎：默认立即返回「t-{id}」，也可指定异常或延迟。</summary>
    private sealed class FakeTranslator : ITranslator
    {
        private readonly Func<CancellationToken, Task<TranslationResult>>? _run;

        public FakeTranslator(
            string id,
            bool isConfigured = true,
            Func<CancellationToken, Task<TranslationResult>>? run = null)
        {
            Id = id;
            IsConfigured = isConfigured;
            _run = run;
        }

        public string Id { get; }

        public string Name => Id.ToUpperInvariant();

        public bool IsConfigured { get; }

        public int CallCount { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _run is null
                ? Task.FromResult(new TranslationResult($"t-{Id}", null))
                : _run(cancellationToken);
        }
    }

    private static FakeTranslator[] Catalog() =>
    [
        new("bing"),                                    // 无需配置，视为已配置
        new("tencent", isConfigured: false),
        new("baidu"),
        new("azure", isConfigured: false),
        new("deepl"),
    ];

    // ==================== Id 列表解析 ====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , , ")]
    public void ParseIds_BlankInput_IsEmpty(string? input)
    {
        Assert.Empty(EngineComparison.ParseIds(input));
    }

    [Fact]
    public void ParseIds_TrimsDropsEmptyAndRemovesDuplicates()
    {
        Assert.Equal(["baidu", "deepl"], EngineComparison.ParseIds(" baidu , deepl ,, baidu "));
    }

    [Fact]
    public void ParseIds_KeepsUserOrder()
    {
        Assert.Equal(["deepl", "bing"], EngineComparison.ParseIds("deepl,bing"));
    }

    // ==================== 对比引擎集合解析（13.4.1）====================

    [Fact]
    public void ResolveEngines_EmptySetting_UsesCurrentEnginePlusFirstOtherConfigured()
    {
        var engines = EngineComparison.ResolveEngines("", Catalog(), "baidu");

        Assert.Equal(["baidu", "bing"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_CurrentEngineNotConfigured_StartsFromFirstConfigured()
    {
        // 当前引擎是 tencent（未配置）→ 退化为「前两个已配置引擎」
        var engines = EngineComparison.ResolveEngines(null, Catalog(), "tencent");

        Assert.Equal(["bing", "baidu"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_ExplicitIds_KeepConfiguredSelectionInUserOrder()
    {
        var engines = EngineComparison.ResolveEngines("deepl,bing", Catalog(), "baidu");

        Assert.Equal(["deepl", "bing"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_UnconfiguredIds_AreFilteredOut()
    {
        // tencent / azure 未配置 → 只剩 1 个；此时不做补齐（由调用方禁用「对比」按钮并提示）
        var engines = EngineComparison.ResolveEngines("tencent,deepl,azure", Catalog(), "baidu");

        Assert.Equal(["deepl"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_UnknownIdsOnly_FallsBackToAuto()
    {
        var engines = EngineComparison.ResolveEngines("nope,ghost", Catalog(), "baidu");

        Assert.Equal(["baidu", "bing"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_CapsAtThreeEngines()
    {
        var engines = EngineComparison.ResolveEngines("bing,baidu,deepl,bing,baidu", Catalog(), "bing");

        Assert.Equal(3, engines.Count);
        Assert.Equal(["bing", "baidu", "deepl"], engines.Select(e => e.Id));
    }

    [Fact]
    public void ResolveEngines_NoConfiguredEngine_ReturnsEmpty()
    {
        FakeTranslator[] catalog = [new("tencent", isConfigured: false), new("baidu", isConfigured: false)];

        Assert.Empty(EngineComparison.ResolveEngines("", catalog, "baidu"));
    }

    [Fact]
    public void ResolveEngines_OnlyOneConfiguredEngine_ReturnsThatOne()
    {
        FakeTranslator[] catalog = [new("bing"), new("baidu", isConfigured: false)];

        Assert.Equal(["bing"], EngineComparison.ResolveEngines("", catalog, "bing").Select(e => e.Id));
    }

    [Fact]
    public void Signature_IsStableAndOrderSensitive()
    {
        Assert.Equal("bing,baidu", EngineComparison.Signature([new FakeTranslator("bing"), new FakeTranslator("baidu")]));
        Assert.NotEqual(
            EngineComparison.Signature([new FakeTranslator("bing"), new FakeTranslator("baidu")]),
            EngineComparison.Signature([new FakeTranslator("baidu"), new FakeTranslator("bing")]));
    }

    // ==================== 并发聚合与部分失败（13.4.3）====================

    [Fact]
    public async Task RunAsync_AllSucceed_ReportsEachEngine()
    {
        var completed = new List<(int Index, EngineComparisonOutcome Outcome)>();

        await EngineComparison.RunAsync(
            [new FakeTranslator("bing"), new FakeTranslator("baidu")],
            "hello", "auto", "zh-CN",
            (index, outcome) => completed.Add((index, outcome)));

        Assert.Equal(2, completed.Count);
        Assert.Equal([0, 1], completed.Select(c => c.Index).Order());
        Assert.Equal("t-bing", completed[0].Outcome.Text);
        Assert.Equal("t-baidu", completed[1].Outcome.Text);
        Assert.All(completed, c => Assert.False(c.Outcome.IsFailure));
    }

    [Fact]
    public async Task RunAsync_OneEngineFails_OthersStillSucceed()
    {
        var failing = new FakeTranslator("baidu", run: _ =>
            Task.FromException<TranslationResult>(
                new TranslationException(TranslationErrorType.Network, "网络不可达")));
        var completed = new List<(int Index, EngineComparisonOutcome Outcome)>();
        var gate = new object(); // 3 个引擎的完成回调并发触发，加锁避免 List.Add 丢失更新

        // 部分失败必须优雅呈现：本方法不抛异常（13.4.3）
        await EngineComparison.RunAsync(
            [new FakeTranslator("bing"), failing, new FakeTranslator("deepl")],
            "hello", "auto", "zh-CN",
            (index, outcome) =>
            {
                lock (gate)
                {
                    completed.Add((index, outcome));
                }
            });

        Assert.Equal(3, completed.Count);
        Assert.True(completed[0].Outcome.HasText);          // 快引擎不受影响
        Assert.True(completed[1].Outcome.IsFailure);
        Assert.Equal(TranslationErrorType.Network, completed[1].Outcome.ErrorType);
        Assert.False(completed[1].Outcome.HasText);
        Assert.True(completed[2].Outcome.HasText);
    }

    [Fact]
    public async Task RunAsync_UnexpectedException_IsClassifiedAsEngineFailure()
    {
        var broken = new FakeTranslator("baidu", run: _ =>
            Task.FromException<TranslationResult>(new InvalidOperationException("boom")));
        var completed = new List<EngineComparisonOutcome>();

        await EngineComparison.RunAsync(
            [broken], "hello", "auto", "zh-CN", (_, outcome) => completed.Add(outcome));

        var outcome = Assert.Single(completed);
        Assert.Equal(TranslationErrorType.Engine, outcome.ErrorType);
        Assert.True(outcome.IsFailure);
    }

    [Fact]
    public async Task RunAsync_Cancelled_ReportsCancelledWithoutError()
    {
        using var cts = new CancellationTokenSource();
        // 回调在各自引擎的续体线程上并发触发，必须用线程安全容器，否则 List.Add 会丢失更新（测试 flaky）
        var completed = new System.Collections.Concurrent.ConcurrentBag<EngineComparisonOutcome>();
        async Task<TranslationResult> Slow(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return new TranslationResult("never", null);
        }

        var run = EngineComparison.RunAsync(
            [new FakeTranslator("bing", run: Slow), new FakeTranslator("baidu", run: Slow)],
            "hello", "auto", "zh-CN",
            (_, outcome) => completed.Add(outcome),
            cts.Token);

        cts.Cancel();
        await run; // 取消不得抛出：关窗/退出对比时不留未观察异常

        Assert.Equal(2, completed.Count);
        Assert.All(completed, outcome =>
        {
            Assert.True(outcome.IsCancelled);
            Assert.False(outcome.IsFailure);
            Assert.Null(outcome.ErrorType);
            Assert.False(outcome.HasText);
        });
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelledToken_NeverCallsEngines()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = new FakeTranslator("bing", run: token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new TranslationResult("t", null));
        });
        var completed = new List<EngineComparisonOutcome>();

        await EngineComparison.RunAsync(
            [engine], "hello", "auto", "zh-CN", (_, outcome) => completed.Add(outcome), cts.Token);

        Assert.True(Assert.Single(completed).IsCancelled);
    }

    [Fact]
    public async Task RunAsync_WithoutCallback_CompletesSilently()
    {
        await EngineComparison.RunAsync(
            [new FakeTranslator("bing"), new FakeTranslator("baidu")],
            "hello", "auto", "zh-CN", onCompleted: null);
    }

    [Fact]
    public async Task RunAsync_RunsEnginesConcurrently()
    {
        // 两个引擎各等 150ms：并发总耗时明显小于串行（300ms）
        var gate = new TaskCompletionSource();
        var started = 0;
        Func<CancellationToken, Task<TranslationResult>> wait = async _ =>
        {
            Interlocked.Increment(ref started);
            await gate.Task;
            return new TranslationResult("t", null);
        };

        var run = EngineComparison.RunAsync(
            [new FakeTranslator("bing", run: wait), new FakeTranslator("baidu", run: wait)],
            "hello", "auto", "zh-CN", null);

        await Task.Delay(100); // 给两个引擎进入等待的机会
        Assert.Equal(2, Volatile.Read(ref started));

        gate.SetResult();
        await run;
    }
}
