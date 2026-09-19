using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// OCR 行文本合并（13.2.2）、语言码映射与语言包缺失/回退判定（13.2.5）。
/// </summary>
public sealed class OcrLanguageTests
{
    private static readonly OcrLanguageTag[] MachineLanguages =
    [
        new("en-US", "英语(美国)"),
        new("ja", "日语"),
        new("zh-Hans-CN", "简体中文(中国大陆)"),
    ];

    // ---------- 行文本合并 ----------

    [Fact]
    public void Compose_JoinsLinesWithNewline()
    {
        Assert.Equal("第一行\n第二行\nThird", OcrTextComposer.Compose(["第一行", "第二行", "Third"]));
    }

    [Fact]
    public void Compose_SkipsBlankLinesAndTrimsEachLine()
    {
        Assert.Equal("hello\nworld", OcrTextComposer.Compose(["  hello ", "", "   ", null, "world"]));
    }

    [Fact]
    public void Compose_WithNoContent_ReturnsEmpty()
    {
        Assert.Equal("", OcrTextComposer.Compose([]));
        Assert.Equal("", OcrTextComposer.Compose(["", "  "]));
    }

    // ---------- 语言码映射 ----------

    [Theory]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("zh", "zh-CN")]
    [InlineData("en-US", "en")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("fr-FR", "fr")]
    [InlineData("de-DE", "de")]
    [InlineData("ru-RU", "ru")]
    [InlineData("es-ES", "es")]
    public void TryMapToTranslationCode_MapsKnownTags(string tag, string expected) =>
        Assert.Equal(expected, OcrLanguages.TryMapToTranslationCode(tag));

    [Theory]
    [InlineData("zh-Hant-TW")] // 繁体不在本程序语言列表内 → 交回引擎自动检测
    [InlineData("pt-BR")]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void TryMapToTranslationCode_UnmappedReturnsNull(string? tag) =>
        Assert.Null(OcrLanguages.TryMapToTranslationCode(tag));

    // ---------- 设置值解析 ----------

    [Fact]
    public void Resolve_WithNoPack_ReportsUnavailable()
    {
        var status = OcrLanguages.Resolve("auto", []);

        Assert.False(status.IsAvailable);
        Assert.Equal(OcrLanguages.Auto, status.SelectedTag);
        Assert.Null(status.TranslationCode);
    }

    [Fact]
    public void Resolve_Auto_KeepsTranslationAutoDetect()
    {
        var status = OcrLanguages.Resolve("auto", MachineLanguages);

        Assert.True(status.IsAvailable);
        Assert.Equal("auto", status.SelectedTag);
        Assert.False(status.IsFallback);
        // 13.2.5 规则 1：OCR 语言为 auto 时由翻译引擎自行检测
        Assert.Null(status.TranslationCode);
    }

    [Fact]
    public void Resolve_ExplicitLanguage_PassesItToTranslator()
    {
        var status = OcrLanguages.Resolve("zh-Hans-CN", MachineLanguages);

        Assert.Equal("zh-Hans-CN", status.SelectedTag);
        Assert.False(status.IsFallback);
        // 13.2.5 规则 2：显式指定 → 作为翻译源语言
        Assert.Equal("zh-CN", status.TranslationCode);
    }

    [Fact]
    public void Resolve_ExplicitLanguageNotInstalled_FallsBackToAuto()
    {
        var status = OcrLanguages.Resolve("fr-FR", MachineLanguages);

        Assert.True(status.IsAvailable);
        Assert.True(status.IsFallback);
        Assert.Equal("auto", status.SelectedTag);
        Assert.Null(status.TranslationCode);
    }

    [Fact]
    public void Resolve_EmptySetting_BehavesLikeAuto()
    {
        var status = OcrLanguages.Resolve("", MachineLanguages);

        Assert.Equal("auto", status.SelectedTag);
        Assert.False(status.IsFallback);
    }

    [Fact]
    public void ShouldSkipTranslation_WhenRecognizedLanguageEqualsTarget()
    {
        var status = OcrLanguages.Resolve("zh-Hans-CN", MachineLanguages);

        Assert.True(status.ShouldSkipTranslation("zh-CN"));
        Assert.False(status.ShouldSkipTranslation("en"));
        // auto 时永不跳过（源语言交给引擎检测）
        Assert.False(OcrLanguages.Resolve("auto", MachineLanguages).ShouldSkipTranslation("zh-CN"));
    }

    // ---------- C-⑥：真实引擎语言（EngineTag）回传 ----------

    /// <summary>
    /// auto 双跑选中 en-US 的场景（中文系统框选英文歌词）：source 一律取
    /// <c>OcrLanguages.TryMapToTranslationCode(实际 EngineTag)</c>——按真实识别器语言
    /// 得到 source="en"，正确送英→中翻译；目标语言恰为英文时按 13.2.5 规则 2 跳过翻译。
    /// </summary>
    [Fact]
    public void EngineTag_映射为真实源语言并参与跳过翻译判定()
    {
        var status = OcrLanguages.Resolve("auto", MachineLanguages); // TranslationCode = null（保持引擎检测）
        var effective = OcrLanguages.TryMapToTranslationCode("en-US");
        Assert.Equal("en", effective);

        var effectiveStatus = status with { TranslationCode = effective };
        Assert.False(effectiveStatus.ShouldSkipTranslation("zh-CN")); // 目标中文：送英→中翻译
        Assert.True(effectiveStatus.ShouldSkipTranslation("en"));     // 目标英文：识别语言==目标语言 → 跳过

        // 对照（修复前）：不回填真实引擎语言时 auto 对英文目标也永不跳过
        Assert.False(status.ShouldSkipTranslation("en"));
    }

    /// <summary>引擎语言映射不出来（未收录语言）时保持原 status 语义：auto = 引擎自动检测。</summary>
    [Fact]
    public void EngineTag_未收录语言时保持原语义()
    {
        var status = OcrLanguages.Resolve("auto", MachineLanguages);

        var effective = OcrLanguages.TryMapToTranslationCode("pt-BR");
        Assert.Null(effective);

        var effectiveStatus = status with { TranslationCode = effective ?? status.TranslationCode };
        Assert.Null(effectiveStatus.TranslationCode);
        Assert.False(effectiveStatus.ShouldSkipTranslation("zh-CN"));
    }

    // ---------- v1.2 修复批 ③-B：映射结果与文本脚本的「对账」 ----------

    /// <summary>zh 引擎把英文识别成 CJK 乱码（映射 zh-CN）而文本实为拉丁 → 交回自动检测，避免误跳过翻译。</summary>
    [Fact]
    public void Reconcile_映射为CJK而文本实为拉丁时_返回null()
    {
        Assert.Null(OcrLanguages.ReconcileWithScript("zh-CN", "Hello world, this is a song lyric line"));
        Assert.Null(OcrLanguages.ReconcileWithScript("ja", "hello world"));
        Assert.Null(OcrLanguages.ReconcileWithScript("ko", "say you say me"));
    }

    /// <summary>反向：en 引擎把中文识别成拉丁碎片（映射 en）而文本实为 CJK → 同样交回自动检测。</summary>
    [Fact]
    public void Reconcile_映射为拉丁而文本实为CJK时_返回null()
    {
        Assert.Null(OcrLanguages.ReconcileWithScript("en", "今天天气真好，我们一起去公园散步吧。"));
        Assert.Null(OcrLanguages.ReconcileWithScript("fr", "这是翻译软件的设置界面"));
    }

    /// <summary>映射与文本方向一致（或中性：纯数字 / 空文本）→ 维持映射结果，不做二次猜测。</summary>
    [Fact]
    public void Reconcile_方向一致或中性时_维持映射结果()
    {
        Assert.Equal("zh-CN", OcrLanguages.ReconcileWithScript("zh-CN", "今天天气真好，我们一起去公园散步吧。"));
        Assert.Equal("en", OcrLanguages.ReconcileWithScript("en", "Hello world, this is a song lyric line"));
        // 中性：纯数字与空文本不参与方向判定
        Assert.Equal("zh-CN", OcrLanguages.ReconcileWithScript("zh-CN", "2026 09 14"));
        Assert.Equal("en", OcrLanguages.ReconcileWithScript("en", ""));
        Assert.Equal("en", OcrLanguages.ReconcileWithScript("en", null));
        // 乱码候选（zh 引擎的 CJK 乱码）与映射 zh 同向 → 维持（由双跑择优负责处理乱码）
        Assert.Equal("zh-CN", OcrLanguages.ReconcileWithScript("zh-CN", "嗯嗯哦哦嗯嗯哦哦嗯嗯嗯哦哦嗯嗯哦嗯嗯哦哦嗯嗯"));
    }

    /// <summary>映射为空（auto / 未收录）时原样返回，交回引擎自动检测。</summary>
    [Fact]
    public void Reconcile_映射为空时_原样返回()
    {
        Assert.Null(OcrLanguages.ReconcileWithScript(null, "Hello world"));
        Assert.Equal(string.Empty, OcrLanguages.ReconcileWithScript(string.Empty, "Hello world"));
    }

    [Fact]
    public void MissingPackMessage_ContainsInstallGuidance()
    {
        Assert.Contains("时间和语言", OcrLanguages.MissingPackMessage);
        Assert.Contains("光学字符识别", OcrLanguages.MissingPackMessage);
    }

    [Fact]
    public void DescribeFallback_ListsAvailableLanguages()
    {
        var message = OcrLanguages.DescribeFallback(MachineLanguages);

        Assert.Contains("回退", message);
        Assert.Contains("简体中文(中国大陆)", message);
        Assert.Contains("英语(美国)", message);
    }

    [Fact]
    public void DescribeFallback_WithNoLanguage_ReturnsMissingPackMessage() =>
        Assert.Equal(OcrLanguages.MissingPackMessage, OcrLanguages.DescribeFallback([]));
}
