using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// AI 通用接口（0.3.2 / FR-022）：双协议（chat / responses）、多供应商档、鉴权开关、
/// 自定义请求头、模型列表解析与 Responses 响应解析。全部用固定向量，不发起真实请求。
/// </summary>
public class AiProviderTests
{
    private const string ApiKey = "sk-fixed-vector-0123456789";

    private static LlmTranslator CreateTranslator(
        string? baseUrl = LlmTranslator.DefaultBaseUrl,
        string model = LlmTranslator.DefaultModel,
        string? key = ApiKey,
        LlmWireApi wireApi = LlmWireApi.Chat,
        bool requiresAuth = true,
        string extraHeaders = "")
    {
        var settings = new AppSettings
        {
            LlmActiveProviderId = "test",
            LlmProviders =
            [
                new LlmProvider
                {
                    Id = "test",
                    Name = "测试档",
                    BaseUrl = baseUrl ?? "",
                    Model = model,
                    ApiKeyEncrypted = SecretStore.Protect(key ?? ""),
                    WireApi = wireApi,
                    RequiresAuth = requiresAuth,
                    ExtraHeadersJson = extraHeaders,
                },
            ],
        };

        return new LlmTranslator(settings, new HttpClientProvider());
    }

    // ==================== 多档：当前档才是真正生效的那一档 ====================

    [Fact]
    public void Request_UsesActiveProvider_NotTheFirstOne()
    {
        var settings = new AppSettings
        {
            LlmActiveProviderId = "second",
            LlmProviders =
            [
                new LlmProvider
                {
                    Id = "first", Name = "一", BaseUrl = "https://a.example/v1",
                    Model = "m-a", ApiKeyEncrypted = SecretStore.Protect("k-a"),
                },
                new LlmProvider
                {
                    Id = "second", Name = "二", BaseUrl = "https://b.example/v1",
                    Model = "m-b", ApiKeyEncrypted = SecretStore.Protect("k-b"),
                },
            ],
        };

        var translator = new LlmTranslator(settings, new HttpClientProvider());
        using var request = translator.CreateRequest("hello", "auto", "zh-CN");

        Assert.Equal("https://b.example/v1/chat/completions", request.RequestUri!.ToString());
        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal("Bearer k-b", Assert.Single(values));
    }

    [Fact]
    public void ActiveLlmProvider_MissingId_FallsBackToFirstProvider()
    {
        var settings = new AppSettings
        {
            LlmActiveProviderId = "不存在的档",
            LlmProviders = [LlmProvider.CreateDefault(), LlmProvider.CreateBlank()],
        };

        Assert.Equal(LlmProvider.DefaultId, settings.ActiveLlmProvider!.Id);
    }

    [Fact]
    public void ActiveLlmProvider_NoProviders_IsNull()
    {
        var settings = new AppSettings { LlmProviders = [] };

        Assert.Null(settings.ActiveLlmProvider);
    }

    // ==================== 双协议：地址归一化 ====================

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/responses")]
    [InlineData("https://api.openai.com/v1/responses", "https://api.openai.com/v1/responses")]
    // 从 chat 形态的完整地址切到 responses，也不会拼出双份后缀
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com/v1/responses")]
    public void NormalizeEndpoint_ResponsesProtocol_AppendsResponsesPath(string input, string expected)
    {
        Assert.Equal(expected, LlmTranslator.NormalizeEndpoint(input, LlmWireApi.Responses));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/models")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/models")]
    [InlineData("", "")]
    public void ModelsEndpoint_NormalizesLikeTheChatEndpoint(string input, string expected)
    {
        Assert.Equal(expected, LlmTranslator.ModelsEndpoint(input));
    }

    // ==================== 双协议：请求体 ====================

    [Fact]
    public async Task CreateRequest_ResponsesProtocol_UsesInstructionsAndInput()
    {
        using var request = CreateTranslator(wireApi: LlmWireApi.Responses)
            .CreateRequest("hello world", "en", "zh-CN");

        Assert.Equal("https://api.deepseek.com/v1/responses", request.RequestUri!.ToString());

        var json = await request.Content!.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
        Assert.Equal("hello world", root.GetProperty("input").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        // 不把用户文本留在第三方：显式 store=false
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Contains("中文（简体）", root.GetProperty("instructions").GetString());
        Assert.False(root.TryGetProperty("messages", out _));
    }

    [Fact]
    public void BuildResponsesRequestBody_TemperatureIsCarried()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            LlmTranslator.BuildResponsesRequestBody("m", "sys", "txt", 0.9));

        Assert.Equal(0.9, document.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal("sys", document.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("txt", document.RootElement.GetProperty("input").GetString());
    }

    // ==================== 鉴权开关与自定义请求头 ====================

    [Fact]
    public void CreateRequest_NoAuth_OmitsAuthorizationHeader()
    {
        using var request = CreateTranslator(key: "", requiresAuth: false, baseUrl: "http://localhost:11434/v1")
            .CreateRequest("hello", "auto", "zh-CN");

        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public void IsConfigured_NoAuth_DoesNotRequireKey()
    {
        Assert.True(CreateTranslator(key: "", requiresAuth: false).IsConfigured);
        Assert.False(CreateTranslator(key: "", requiresAuth: true).IsConfigured);
    }

    [Fact]
    public void CreateRequest_ExtraHeaders_AreApplied_ButBuiltInAuthWins()
    {
        using var request = CreateTranslator(extraHeaders: """{"X-Org":"team","Authorization":"Bearer forged"}""")
            .CreateRequest("hello", "auto", "zh-CN");

        Assert.True(request.Headers.TryGetValues("X-Org", out var org));
        Assert.Equal("team", Assert.Single(org));

        Assert.True(request.Headers.TryGetValues("Authorization", out var auth));
        Assert.Equal($"Bearer {ApiKey}", Assert.Single(auth));
    }

    [Fact]
    public void CreateRequest_NoAuth_AllowsUserSuppliedAuthorizationHeader()
    {
        using var request = CreateTranslator(
                key: "", requiresAuth: false, extraHeaders: """{"Authorization":"Api-Key hand-written"}""")
            .CreateRequest("hello", "auto", "zh-CN");

        Assert.True(request.Headers.TryGetValues("Authorization", out var auth));
        Assert.Equal("Api-Key hand-written", Assert.Single(auth));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("不是 JSON")]
    [InlineData("[1,2]")]
    public void ParseExtraHeaders_BadInput_ReturnsEmpty(string json)
    {
        Assert.Empty(LlmTranslator.ParseExtraHeaders(json));
    }

    [Fact]
    public void ParseExtraHeaders_NonStringValue_KeepsRawJson()
    {
        var headers = LlmTranslator.ParseExtraHeaders("""{"X-Retry":3,"X-On":true}""");

        Assert.Contains(headers, h => h.Key == "X-Retry" && h.Value == "3");
        Assert.Contains(headers, h => h.Key == "X-On" && h.Value == "true");
    }

    // ==================== 模型列表解析 ====================

    [Fact]
    public void ParseModelIds_OpenAiShape()
    {
        var ids = LlmTranslator.ParseModelIds("""{"data":[{"id":"gpt-4o"},{"id":"deepseek-chat"}]}""");

        Assert.Equal(new[] { "gpt-4o", "deepseek-chat" }, ids);
    }

    [Fact]
    public void ParseModelIds_OllamaShape_AndBareArray_AreDeduped()
    {
        Assert.Equal(
            new[] { "llama3", "qwen2" },
            LlmTranslator.ParseModelIds("""{"models":[{"name":"llama3"},{"name":"llama3"},{"name":"qwen2"}]}"""));

        Assert.Equal(new[] { "a", "b" }, LlmTranslator.ParseModelIds("""["a","b"]"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"data":"不是数组"}""")]
    public void ParseModelIds_Unrecognized_ReturnsEmpty(string json)
    {
        Assert.Empty(LlmTranslator.ParseModelIds(json));
    }

    // ==================== Responses 响应解析 ====================

    [Fact]
    public void ParseResponses_ReadsOutputTextParts()
    {
        const string json = """
        {"output":[{"type":"message","content":[{"type":"output_text","text":"你好"},{"type":"output_text","text":"，世界"}]}]}
        """;

        // 取第一段非空 output_text（分段拼接是流式场景的事，这里是非流式一次性响应）
        Assert.Equal("你好", LlmResponseParser.ParseResponses(json));
    }

    [Fact]
    public void ParseResponses_FallsBackToTopLevelOutputText()
    {
        Assert.Equal("备用", LlmResponseParser.ParseResponses("""{"output_text":"备用"}"""));
    }

    [Fact]
    public void ParseResponses_FallsBackToChatShape()
    {
        Assert.Equal(
            "聊天形态",
            LlmResponseParser.ParseResponses("""{"choices":[{"message":{"content":"聊天形态"}}]}"""));
    }

    // ==================== 自建模型列表（手填加入） ====================

    [Fact]
    public void MergeModels_CustomFirst_ThenFetched()
    {
        var merged = LlmProvider.MergeModels(["自建A"], ["接口B", "接口C"]);

        Assert.Equal(["自建A", "接口B", "接口C"], merged);
    }

    [Fact]
    public void MergeModels_DedupesIgnoringCase_AndTrims()
    {
        var merged = LlmProvider.MergeModels([" GPT-4o ", "gpt-4o"], ["GPT-4O", "   ", "o3"]);

        Assert.Equal(["GPT-4o", "o3"], merged);
    }

    [Fact]
    public void MergeModels_NullSides_AreTreatedAsEmpty()
    {
        Assert.Equal(["only"], LlmProvider.MergeModels(null, ["only"]));
        Assert.Equal(["only"], LlmProvider.MergeModels(["only"], null));
        Assert.Empty(LlmProvider.MergeModels(null, null));
    }

    [Fact]
    public void Clone_DeepCopiesCustomModels()
    {
        var source = new LlmProvider { Id = "p1", CustomModels = ["a"] };

        var clone = source.Clone();
        clone.CustomModels.Add("b");

        // 浅拷贝会让两档共用同一个列表，改一档就把另一档也改了
        Assert.Equal(["a"], source.CustomModels);
        Assert.Equal(["a", "b"], clone.CustomModels);
    }

    [Fact]
    public void CustomModels_JsonRoundTrip()
    {
        var source = new LlmProvider { Id = "p1", Name = "档", Model = "m", CustomModels = ["自建A", "自建B"] };

        var json = System.Text.Json.JsonSerializer.Serialize(source);
        var back = System.Text.Json.JsonSerializer.Deserialize<LlmProvider>(json)!;

        Assert.Equal(["自建A", "自建B"], back.CustomModels);
    }

    [Fact]
    public void CustomModels_MissingInJson_DefaultsToEmpty()
    {
        // 旧版 settings.json 里没有这个字段，读出来必须是空列表而不是 null
        var back = System.Text.Json.JsonSerializer.Deserialize<LlmProvider>(
            """{"Id":"p1","Name":"档","BaseUrl":"","ApiKeyEncrypted":"","Model":"m"}""")!;

        Assert.Empty(back.CustomModels);
    }

    // ==================== 自建列表拖动排序 ====================

    [Fact]
    public void MoveCustomModelToIndex_DraggedDown_LandsAtTargetIndex()
    {
        var provider = new LlmProvider { Id = "p1", CustomModels = ["A", "B", "C"] };

        Assert.True(provider.MoveCustomModelToIndex("A", 2));
        Assert.Equal(["B", "C", "A"], provider.CustomModels);
    }

    [Fact]
    public void MoveCustomModelToIndex_DraggedUp_LandsAtTargetIndex()
    {
        var provider = new LlmProvider { Id = "p1", CustomModels = ["A", "B", "C"] };

        Assert.True(provider.MoveCustomModelToIndex("C", 0));
        Assert.Equal(["C", "A", "B"], provider.CustomModels);
    }

    [Fact]
    public void MoveCustomModelToIndex_IgnoresCase()
    {
        var provider = new LlmProvider { Id = "p1", CustomModels = ["gpt-4o", "B", "C"] };

        Assert.True(provider.MoveCustomModelToIndex("GPT-4O", 2));
        Assert.Equal(["B", "C", "gpt-4o"], provider.CustomModels);
    }

    [Fact]
    public void MoveCustomModelToIndex_UnknownOrSame_KeepsOrder()
    {
        var provider = new LlmProvider { Id = "p1", CustomModels = ["A", "B"] };

        // 拖的是接口拉回的那份（不在自建列表里）、拖到自己那一格、或名字为空：一律不动
        Assert.False(provider.MoveCustomModelToIndex("只存在于接口拉回的那份", 0));
        Assert.False(provider.MoveCustomModelToIndex("A", 0));
        Assert.False(provider.MoveCustomModelToIndex(null, 1));
        Assert.Equal(["A", "B"], provider.CustomModels);
    }

    [Fact]
    public void MoveCustomModelToIndex_ClampsOutOfRange()
    {
        var provider = new LlmProvider { Id = "p1", CustomModels = ["A", "B", "C"] };

        // 落到列表末尾以外（比如拖到弹层下缘之外）：当成拖到最后
        Assert.True(provider.MoveCustomModelToIndex("A", 99));
        Assert.Equal(["B", "C", "A"], provider.CustomModels);
    }

    [Theory]
    // 拖动条在下标 from（自建列表里），插入线落在合并列表的 dropSlot；自建共 customCount 条
    [InlineData(0, 3, 3, 2)]   // 往下拖到 C 的下缘：A 落在 C 后面
    [InlineData(0, 2, 3, 1)]   // 往下拖到 C 的上缘：A 落在 B、C 之间
    [InlineData(2, 0, 3, 0)]   // 往上拖到 A 的上缘：C 落在最前
    [InlineData(2, 1, 3, 1)]   // 往上拖到 A 的下缘：C 落在 A 后面
    [InlineData(0, 1, 3, 0)]   // 就落在自己旁边：等于没动
    [InlineData(1, 1, 3, 1)]   // 同理：等于没动
    [InlineData(0, 9, 3, 2)]   // 拖到接口拉回那一段上：算拖到自建列表末尾
    [InlineData(2, 9, 3, 2)]   // 本来就在末尾：不动
    [InlineData(1, 0, 1, 0)]   // 只有一条自建：怎么拖都是它自己
    public void IndexForDropSlot_MapsDropLineToFinalIndex(int from, int dropSlot, int customCount, int expected) =>
        Assert.Equal(expected, LlmProvider.IndexForDropSlot(from, dropSlot, customCount));
}
