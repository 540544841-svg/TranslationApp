using Dapper;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 批 5a 核心测试（FR-050 语境化 / FR-051 换说法 / FR-054 写作档）：
/// Prompt 追加规则与截尾、指令通道与装饰器转发、语境取数规则、档案第 9 键。
/// 全程不发起真实网络请求。
/// </summary>
public sealed class Batch5aCoreTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ta-b5-{Guid.NewGuid():N}.db");
    private readonly HistoryDatabase _database;
    private readonly HistoryRepository _history;

    public Batch5aCoreTests()
    {
        _database = new HistoryDatabase(_databasePath);
        _database.Initialize();
        _history = new HistoryRepository(_database);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_databasePath + suffix); } catch (IOException) { /* 清理失败不影响结论 */ }
        }
    }

    // ==================== FR-051 风格的持久化键与文案 ====================

    [Theory]
    [InlineData("none", TranslationStyle.None)]
    [InlineData("colloquial", TranslationStyle.Colloquial)]
    [InlineData("formal", TranslationStyle.Formal)]
    [InlineData("concise", TranslationStyle.Concise)]
    [InlineData("", TranslationStyle.None)]
    [InlineData("未定义的值", TranslationStyle.None)]
    public void StyleKey_RoundTrips_AndFallsBackToNone(string key, TranslationStyle expected) =>
        Assert.Equal(expected, TranslationStyles.Parse(key));

    [Fact]
    public void StyleKey_NoneAndNamedValues_RoundTripThroughSetting()
    {
        foreach (var style in new[] { TranslationStyle.None, TranslationStyle.Colloquial, TranslationStyle.Formal, TranslationStyle.Concise })
        {
            Assert.Equal(style, TranslationStyles.Parse(style.ToSettingKey()));
        }
    }

    [Fact]
    public void StyleDisplayName_MatchesButtonCopy()
    {
        Assert.Equal("原样", TranslationStyle.None.DisplayName());
        Assert.Equal("更口语", TranslationStyle.Colloquial.DisplayName());
        Assert.Equal("更正式", TranslationStyle.Formal.DisplayName());
        Assert.Equal("更简短", TranslationStyle.Concise.DisplayName());
    }

    // ==================== FR-050 / FR-051 Prompt 构造 ====================

    private static string Build(
        TranslationDirective directive,
        string customPrompt = "",
        string source = "auto",
        string target = "zh-CN") =>
        LlmPrompt.Build(customPrompt, source, target, "当前这段文本", directive);

    [Fact]
    public void BuiltInPrompt_NoDirective_UnchangedFromBefore()
    {
        var prompt = Build(TranslationDirective.None);

        Assert.Contains("你是专业的翻译引擎", prompt);
        Assert.DoesNotContain("上一段原文", prompt);
        Assert.DoesNotContain("风格要求", prompt);
    }

    [Fact]
    public void BuiltInPrompt_WithContext_AppendsContextLineThatForbidsTranslatingIt()
    {
        var prompt = Build(new TranslationDirective("这是上一段原文", TranslationStyle.None));

        Assert.Contains("上一段原文", prompt);
        Assert.Contains("这是上一段原文", prompt);
        Assert.Contains("不要翻译它", prompt);
    }

    [Fact]
    public void BuiltInPrompt_LongContext_TruncatedToTail()
    {
        var context = new string('头', 700);
        var prompt = Build(new TranslationDirective(context, TranslationStyle.None));

        Assert.Contains(new string('头', 600), prompt);
        Assert.DoesNotContain(new string('头', 601), prompt);
    }

    [Fact]
    public void BuiltInPrompt_BlankContext_NotAppended()
    {
        Assert.DoesNotContain("上一段原文", Build(new TranslationDirective("   ", TranslationStyle.None)));
    }

    [Theory]
    [InlineData(TranslationStyle.Colloquial, "口语化")]
    [InlineData(TranslationStyle.Formal, "正式、书面")]
    [InlineData(TranslationStyle.Concise, "尽量简短")]
    public void BuiltInPrompt_EachStyleHasItsOwnInstruction(TranslationStyle style, string expectedFragment)
    {
        var prompt = Build(new TranslationDirective(null, style));

        Assert.Contains("风格要求：", prompt);
        Assert.Contains(expectedFragment, prompt);
    }

    [Fact]
    public void BuiltInPrompt_ContextComesBeforeStyle()
    {
        var prompt = Build(new TranslationDirective("上一段", TranslationStyle.Formal));

        Assert.True(prompt.IndexOf("上一段原文", StringComparison.Ordinal)
                    < prompt.IndexOf("风格要求", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomPrompt_WithoutPlaceholders_IgnoresDirectiveEntirely()
    {
        var custom = "把下面内容译成{target}，只输出译文。";
        var prompt = Build(new TranslationDirective("上一段原文", TranslationStyle.Concise), custom);

        // 用户没写占位符 = 不往他的 Prompt 里塞东西（尊重自定义结构）
        Assert.DoesNotContain("上一段原文", prompt);
        Assert.DoesNotContain("风格要求", prompt);
        Assert.DoesNotContain("{", prompt);
    }

    [Theory]
    [InlineData("ctx:{context}|style:{style}|text:{text}")]
    [InlineData("ctx:{语境}|style:{风格}|text:{文本}")]
    public void CustomPrompt_ReplacesPlaceholders_BothSpellings(string custom)
    {
        var prompt = Build(new TranslationDirective("上一段", TranslationStyle.Formal), custom);

        Assert.Equal(
            $"ctx:上一段|style:{LlmPrompt.StyleInstruction(TranslationStyle.Formal)}|text:{LlmPrompt.UserMessageTextReference}",
            prompt);
        Assert.DoesNotContain("当前这段文本", prompt);
    }

    [Fact]
    public void CustomPrompt_MissingValues_ReplacedWithEmpty_NotLeftAsBraces()
    {
        var prompt = Build(TranslationDirective.None, "ctx={context} style={style}");

        Assert.Equal("ctx= style=", prompt);
    }

    // ==================== 指令通道：能力判定与装饰器转发 ====================

    private sealed class PlainTranslator : ITranslator
    {
        public string Id => "plain";
        public string Name => "Plain";
        public bool IsConfigured => true;

        public Task<TranslationResult> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranslationResult(text.ToUpperInvariant(), null));
    }

    private sealed class DirectableTranslator : ITranslator, IPromptDirectiveTranslator
    {
        public string Id => "llm";
        public string Name => "Directable";
        public bool IsConfigured => true;
        public TranslationDirective Received { get; private set; }

        public Task<TranslationResult> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            Received = TranslationDirective.None;
            return Task.FromResult(new TranslationResult(text.ToUpperInvariant(), null));
        }

        public Task<TranslationResult> TranslateAsync(
            string text, string sourceLanguage, string targetLanguage, TranslationDirective directive,
            CancellationToken cancellationToken = default)
        {
            Received = directive;
            return Task.FromResult(new TranslationResult(text.ToUpperInvariant(), null));
        }
    }

    [Fact]
    public void SupportsDirectives_UnwrapsDecorator_ThenJudgesRealEngine()
    {
        var directable = new GlossaryTranslator(new DirectableTranslator(), () => Array.Empty<GlossaryItem>());
        var plain = new GlossaryTranslator(new PlainTranslator(), () => Array.Empty<GlossaryItem>());

        // 装饰器恒转发指令，能力判定必须落在被包的真实引擎上——否则 Bing 也会长出「换说法」按钮
        Assert.True(TranslatorCatalog.SupportsDirectives(directable));
        Assert.False(TranslatorCatalog.SupportsDirectives(plain));
    }

    [Fact]
    public async Task Decorator_ForwardsDirective_AndStillAppliesGlossary()
    {
        var inner = new DirectableTranslator();
        var sut = new GlossaryTranslator(
            inner,
            () => [new GlossaryItem("model", "模型")]);

        var result = await sut.TranslateAsync(
            "the model is big", "auto", "zh-CN", new TranslationDirective("上一段", TranslationStyle.Formal));

        Assert.Equal(new TranslationDirective("上一段", TranslationStyle.Formal), inner.Received);
        Assert.Equal("THE 模型 IS BIG", result.TranslatedText);
        Assert.Equal(1, result.GlossaryHits);
    }

    [Fact]
    public async Task Decorator_NoDirectivePath_SendsNoneNotGarbage()
    {
        var inner = new DirectableTranslator();
        var sut = new GlossaryTranslator(inner, () => Array.Empty<GlossaryItem>());

        await sut.TranslateAsync("abc", "auto", "zh-CN");

        Assert.Equal(TranslationDirective.None, inner.Received);
    }

    [Fact]
    public async Task Decorator_EngineWithoutDirectiveSupport_IgnoresDirective_AndRecordsStats()
    {
        var log = new List<EngineOutcome>();
        var sut = new GlossaryTranslator(
            new PlainTranslator(),
            () => Array.Empty<GlossaryItem>(),
            recordOutcome: (_, outcome, _, _) => log.Add(outcome));

        var result = await sut.TranslateAsync("abc", "auto", "zh-CN", new TranslationDirective("上一段", TranslationStyle.Concise));

        Assert.Equal("ABC", result.TranslatedText);
        Assert.Single(log);
        Assert.Equal(EngineOutcome.Success, log[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Directive_CarriesThroughBothCallShapes(bool empty)
    {
        var directive = empty ? default : new TranslationDirective("ctx", TranslationStyle.Formal);

        Assert.Equal(empty, directive.IsEmpty);
        Assert.True(TranslationDirective.None.IsEmpty);
        Assert.False(new TranslationDirective(null, TranslationStyle.Concise).IsEmpty);
    }

    // ==================== FR-050 LLM 请求体：语境只进 system ====================

    private static LlmTranslator CreateTranslator(string prompt = "") =>
        new(new AppSettings
        {
            LlmBaseUrl = LlmTranslator.DefaultBaseUrl,
            LlmModel = LlmTranslator.DefaultModel,
            LlmApiKeyEncrypted = SecretStore.Protect("sk-fixed-vector-0123456789"),
            LlmPrompt = prompt,
        }, new HttpClientProvider());

    [Fact]
    public async Task LlmRequest_UserMessageIsAlwaysTheTextBeingTranslated()
    {
        var request = CreateTranslator().CreateRequest(
            "正文在这里", "auto", "zh-CN", new TranslationDirective("语境在这里", TranslationStyle.Formal));

        var json = await request.Content!.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var system = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var user = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Equal("正文在这里", user);
        Assert.DoesNotContain("语境在这里", user);
        Assert.Contains("语境在这里", system);
        Assert.Contains("风格要求", system);
    }

    // ==================== FR-050 语境来源规则 ====================

    [Fact]
    public void ContextSource_SameTargetLanguage_ReturnsMostRecentSource()
    {
        _history.Add("第一段", "first", "en", "zh-CN", "AI");
        _history.Add("第二段", "second", "en", "zh-CN", "AI");
        _history.Add("别的语言对", "other", "en", "ru", "AI");

        Assert.Equal("第二段", _history.ContextSource("zh-CN", "第三段"));
    }

    [Fact]
    public void ContextSource_SkipsRecordEqualToCurrentInput()
    {
        _history.Add("第一段", "first", "en", "zh-CN", "AI");
        _history.Add("第三段", "third", "en", "zh-CN", "AI");

        // 「同一句译了两遍」时最近一条就是本句，拿它当语境毫无意义 → 往前一条
        Assert.Equal("第一段", _history.ContextSource("zh-CN", "  第三段  "));
    }

    [Fact]
    public void ContextSource_OnlySelfInHistory_ReturnsNull()
    {
        _history.Add("同一句", "same", "en", "zh-CN", "AI");

        Assert.Null(_history.ContextSource("zh-CN", "同一句"));
    }

    [Fact]
    public void ContextSource_EmptyDatabase_ReturnsNull() =>
        Assert.Null(_history.ContextSource("zh-CN", "任意输入"));

    [Fact]
    public void ContextSource_TooOldRecord_Ignored()
    {
        InsertAt("很久以前", "long ago", "zh-CN", DateTimeOffset.UtcNow.AddMinutes(-31));

        Assert.Null(_history.ContextSource("zh-CN", "现在的输入"));
        Assert.Equal("很久以前", _history.ContextSource("zh-CN", "现在的输入", maxAgeMinutes: 60));
    }

    [Fact]
    public void ContextSource_NonPositiveAgeWindow_ReturnsNull()
    {
        _history.Add("第一段", "first", "en", "zh-CN", "AI");

        Assert.Null(_history.ContextSource("zh-CN", "第二段", maxAgeMinutes: 0));
    }

    private void InsertAt(string source, string translated, string targetLanguage, DateTimeOffset at)
    {
        using var connection = _database.OpenConnection();
        connection.Execute(
            """
            INSERT INTO History (CreatedAtMs, SourceText, TranslatedText, SourceLanguage, TargetLanguage, Engine)
            VALUES (@CreatedAtMs, @Source, @Translated, 'en', @TargetLanguage, 'AI');
            """,
            new
            {
                CreatedAtMs = at.ToUnixTimeMilliseconds(),
                Source = source,
                Translated = translated,
                TargetLanguage = targetLanguage,
            });
    }

    // ==================== FR-054 写作档 ====================

    [Fact]
    public void WritingProfile_PinsEngineAndStyle_ButNotTargetLanguage()
    {
        var settings = new AppSettings
        {
            Engine = "bing",
            TargetLanguage = "en",
            SourceLanguage = "auto",
            CleanClipboardText = true,
            TranslationStyle = "none",
            ClipboardMonitorEnabled = true,
        };

        new ProfileService(settings).Apply("写作");

        Assert.Equal("llm", settings.Engine);
        Assert.Equal("formal", settings.TranslationStyle);
        Assert.False(settings.CleanClipboardText);
        Assert.True(settings.GlossaryEnabled);
        // 稀疏语义：没钉的键一律不动（切换档案不该顺手改用户的语言与监听）
        Assert.Equal("en", settings.TargetLanguage);
        Assert.Equal("auto", settings.SourceLanguage);
        Assert.True(settings.ClipboardMonitorEnabled);
        Assert.Equal("写作", settings.ActiveProfile);
    }

    [Fact]
    public void StyleKey_ParticipatesInDeviationCheck()
    {
        // 其余钉住键都对齐，只差风格 → 偏离判定必须能只因风格键报偏离（第 9 键真的接进了三处逻辑）
        var settings = new AppSettings
        {
            Engine = "llm",
            CleanClipboardText = false,
            GlossaryEnabled = true,
            TranslationStyle = "none",
        };
        var profile = new ProfileService(settings).AllProfiles().First(p => p.Name == "写作");

        Assert.True(profile.Overrides.DeviatesFrom(settings));
        settings.TranslationStyle = "formal";
        Assert.False(profile.Overrides.DeviatesFrom(settings));
    }

    [Fact]
    public void SaveCurrentAs_SnapshotsStyleKey()
    {
        var settings = new AppSettings { TranslationStyle = "concise" };
        var service = new ProfileService(settings);

        Assert.True(service.SaveCurrentAs("我的档"));
        settings.TranslationStyle = "none";
        service.Apply("我的档");

        Assert.Equal("concise", settings.TranslationStyle);
    }
}
