using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 联网集成测试：默认跳过，仅在设置环境变量 TRANSLATIONAPP_LIVE_TESTS=1 时执行，
/// 用于按需验证翻译引擎在当前网络下确实可用（避免 CI/离线环境不稳定）。
/// </summary>
public class LiveEngineTests
{
    private static bool LiveTestsEnabled =>
        Environment.GetEnvironmentVariable("TRANSLATIONAPP_LIVE_TESTS") == "1";

    private static HttpClientProvider CreateProvider() => new();

    [Fact]
    public async Task BingEngine_TranslatesEnglishToChinese()
    {
        if (!LiveTestsEnabled)
        {
            return; // 未开启联网测试
        }

        var translator = new BingTranslator(CreateProvider());

        var result = await translator.TranslateAsync(
            "The quick brown fox jumps over the lazy dog.", TranslationLanguages.AutoCode, "zh-CN");

        Assert.False(string.IsNullOrWhiteSpace(result.TranslatedText));
        Assert.Contains("狐狸", result.TranslatedText); // 译文应包含「狐狸」
        Assert.Equal("en", result.DetectedSourceLanguage); // 自动检测源语言
    }

    [Fact]
    public async Task BingEngine_LongText_IsChunkedAndTranslated()
    {
        if (!LiveTestsEnabled)
        {
            return;
        }

        var translator = new BingTranslator(CreateProvider());
        var text = string.Join(" ", Enumerable.Repeat("Hello world.", 120)); // 约 1560 字符，触发分块

        var result = await translator.TranslateAsync(text, "en", "zh-CN");

        Assert.False(string.IsNullOrWhiteSpace(result.TranslatedText));
    }
}
