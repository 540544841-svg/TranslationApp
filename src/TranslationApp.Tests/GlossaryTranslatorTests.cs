using TranslationApp.Core.History;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>引擎装饰器测试（P0 批 1 / spec §1.3 §4.2）：术语替换 + 成败统计 + 隐私/测试连接门控。</summary>
public class GlossaryTranslatorTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public string Id => "fake";
        public string Name => "Fake";
        public bool IsConfigured => true;
        public Func<string, TranslationResult> Handler = t => new(t.ToUpperInvariant(), "en");

        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<TranslationResult>(cancellationToken);
            return Task.FromResult(Handler(text));
        }
    }

    private sealed record Recorded(string EngineId, EngineOutcome Outcome, string? Error);

    private static (GlossaryTranslator sut, List<Recorded> log) Build(
        FakeTranslator inner,
        IReadOnlyList<GlossaryItem>? glossary = null,
        bool privacy = false)
    {
        var log = new List<Recorded>();
        var sut = new GlossaryTranslator(
            inner,
            () => glossary ?? Array.Empty<GlossaryItem>(),
            recordOutcome: (id, outcome, err) => log.Add(new Recorded(id, outcome, err)),
            privacyMode: () => privacy);
        return (sut, log);
    }

    private static GlossaryItem Item(string s, string t) => new(s, t);

    [Fact]
    public async Task Success_AppliesGlossary_AndReportsHits()
    {
        var inner = new FakeTranslator { Handler = _ => new("the MODEL is big", "en") };
        var (sut, log) = Build(inner, new[] { Item("model", "模型") });

        var r = await sut.TranslateAsync("x", "auto", "zh-CN");

        Assert.Equal("the 模型 is big", r.TranslatedText);
        Assert.Equal(1, r.GlossaryHits);
        Assert.Single(r.GlossaryApplied!);
        Assert.Single(log);
        Assert.Equal(EngineOutcome.Success, log[0].Outcome);
    }

    [Fact]
    public async Task Success_NoGlossary_PassesThrough()
    {
        var inner = new FakeTranslator { Handler = _ => new("plain", "en") };
        var (sut, _) = Build(inner);

        var r = await sut.TranslateAsync("x", "auto", "zh-CN");

        Assert.Equal("plain", r.TranslatedText);
        Assert.Equal(0, r.GlossaryHits);
    }

    [Fact]
    public async Task TranslationException_RethrowsAndRecordsByType()
    {
        var inner = new FakeTranslator();
        inner.Handler = _ => throw new TranslationException(TranslationErrorType.QuotaExceeded, "HTTP 429");
        var (sut, log) = Build(inner);

        var ex = await Assert.ThrowsAsync<TranslationException>(() => sut.TranslateAsync("x", "auto", "zh-CN"));

        Assert.Equal(TranslationErrorType.QuotaExceeded, ex.ErrorType);
        Assert.Single(log);
        Assert.Equal(EngineOutcome.FailQuota, log[0].Outcome);
        Assert.Equal("HTTP 429", log[0].Error);
    }

    [Fact]
    public async Task OtherExceptions_RecordAsFailEngine_AndRethrow()
    {
        var inner = new FakeTranslator();
        inner.Handler = _ => throw new InvalidOperationException("boom");
        var (sut, log) = Build(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TranslateAsync("x", "auto", "zh-CN"));

        Assert.Equal(EngineOutcome.FailEngine, log[0].Outcome);
    }

    [Fact]
    public async Task Cancellation_RecordsNothing()
    {
        var inner = new FakeTranslator();
        var (sut, log) = Build(inner);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.TranslateAsync("x", "auto", "zh-CN", cts.Token));

        Assert.Empty(log);
    }

    [Fact]
    public async Task PrivacyMode_SkipsStatsEntirely()
    {
        var inner = new FakeTranslator();
        var (sut, log) = Build(inner, privacy: true);

        await sut.TranslateAsync("x", "auto", "zh-CN");

        Assert.Empty(log);
    }

    [Fact]
    public async Task SuppressStatsScope_SkipsRecordingButKeepsGlossary()
    {
        var inner = new FakeTranslator { Handler = _ => new("the model", "en") };
        var (sut, log) = Build(inner, new[] { Item("model", "模型") });

        using (GlossaryTranslator.SuppressStats())
        {
            var r = await sut.TranslateAsync("x", "auto", "zh-CN");
            Assert.Equal("the 模型", r.TranslatedText);
        }

        Assert.Empty(log);
        await sut.TranslateAsync("x", "auto", "zh-CN");
        Assert.Single(log); // 作用域外恢复记录
    }

    [Fact]
    public async Task NullStatsSink_StillAppliesGlossary()
    {
        var inner = new FakeTranslator { Handler = _ => new("the model", "en") };
        var sut = new GlossaryTranslator(inner, () => new[] { Item("model", "模型") });

        var r = await sut.TranslateAsync("x", "auto", "zh-CN");

        Assert.Equal("the 模型", r.TranslatedText);
    }

    [Fact]
    public void Metadata_PassesThrough()
    {
        var (sut, _) = Build(new FakeTranslator());

        Assert.Equal("fake", sut.Id);
        Assert.Equal("Fake", sut.Name);
        Assert.True(sut.IsConfigured);
    }
}
