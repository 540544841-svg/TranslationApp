using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>引擎目录与切换测试（FR-006）。</summary>
public class TranslatorCatalogTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public FakeTranslator(string id, bool isConfigured)
        {
            Id = id;
            Name = id;
            IsConfigured = isConfigured;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsConfigured { get; }

        public Task<TranslationResult> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult($"translated:{text}", sourceLanguage));
    }

    [Fact]
    public void Resolve_ReturnsEngineMatchingId()
    {
        var bing = new FakeTranslator("bing", true);
        var google = new FakeTranslator("google", true);
        var catalog = new TranslatorCatalog([bing, google]);

        Assert.Same(google, catalog.Resolve("google"));
        Assert.Same(bing, catalog.Resolve("bing"));
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToFirstConfigured()
    {
        var bing = new FakeTranslator("bing", true);
        var catalog = new TranslatorCatalog([bing]);

        Assert.Same(bing, catalog.Resolve("不存在的引擎"));
        Assert.Same(bing, catalog.Resolve(null));
    }

    [Fact]
    public void Resolve_SelectedEngineNotConfigured_FallsBackToConfiguredEngine()
    {
        var bing = new FakeTranslator("bing", true);
        var tencent = new FakeTranslator("tencent", false); // 未配置 Key
        var catalog = new TranslatorCatalog([bing, tencent]);

        Assert.Same(bing, catalog.Resolve("tencent"));
    }

    [Fact]
    public void Constructor_WithoutEngines_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TranslatorCatalog([]));
    }

    [Fact]
    public void All_PreservesRegistrationOrder()
    {
        var bing = new FakeTranslator("bing", true);
        var google = new FakeTranslator("google", true);
        var catalog = new TranslatorCatalog([bing, google]);

        Assert.Equal(["bing", "google"], catalog.All.Select(e => e.Id));
    }
}
