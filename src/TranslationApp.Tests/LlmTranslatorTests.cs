using System.Net;
using System.Text.Json;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// AI 翻译通道测试（FR-022 / 13.3.1 ~ 13.3.2）：BaseURL 归一化的三种形态、请求体与鉴权头、
/// 响应解析（正常 / 缺 choices / 空 content）、错误映射、超时预算、Prompt 构造与占位符。
/// 全部用固定向量，不发起真实请求。
/// </summary>
public class LlmTranslatorTests
{
    private const string ApiKey = "sk-fixed-vector-0123456789";

    private static LlmTranslator CreateTranslator(
        string? baseUrl = LlmTranslator.DefaultBaseUrl,
        string model = LlmTranslator.DefaultModel,
        string? key = ApiKey,
        string prompt = "",
        double temperature = LlmTranslator.DefaultTemperature)
    {
        return CreateTranslator(baseUrl, model, key, prompt, temperature, LlmWireApi.Chat, true, "");
    }

    private static LlmTranslator CreateTranslator(
        string? baseUrl,
        string model,
        string? key,
        string prompt,
        double temperature,
        LlmWireApi wireApi,
        bool requiresAuth,
        string extraHeaders)
    {
        var settings = new AppSettings
        {
            LlmPrompt = prompt,
            LlmTemperature = temperature,
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

    private static (string Model, string SystemPrompt, string UserText, bool Stream, double Temperature) ReadBody(
        HttpRequestMessage request)
    {
        var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return (
            root.GetProperty("model").GetString()!,
            root.GetProperty("messages")[0].GetProperty("content").GetString()!,
            root.GetProperty("messages")[1].GetProperty("content").GetString()!,
            root.GetProperty("stream").GetBoolean(),
            root.GetProperty("temperature").GetDouble());
    }

    // ==================== 引擎标识 ====================

    [Fact]
    public void Engine_IdentifiesItselfAsLlm()
    {
        var translator = CreateTranslator();

        Assert.Equal("llm", translator.Id);
        Assert.Equal("AI 翻译（通用接口）", translator.Name);
    }

    // ==================== BaseURL 归一化（13.3.1）====================

    [Theory]
    // 以 /chat/completions 结尾 → 原样
    [InlineData("https://api.deepseek.com/v1/chat/completions", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/chat/completions/", "https://api.deepseek.com/v1/chat/completions")]
    // 以 /v1 结尾 → 追加 /chat/completions
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1",
        "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")]
    // 其它 → 追加 /v1/chat/completions
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://example.com/api/v2", "https://example.com/api/v2/v1/chat/completions")]
    // 大小写与空白容错
    [InlineData("  https://api.deepseek.com/v1  ", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/V1", "https://api.deepseek.com/V1/chat/completions")]
    public void NormalizeEndpoint_HandlesThreeShapes(string input, string expected)
    {
        Assert.Equal(expected, LlmTranslator.NormalizeEndpoint(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeEndpoint_EmptyInput_ReturnsEmpty(string? input)
    {
        Assert.Equal("", LlmTranslator.NormalizeEndpoint(input));
    }

    // ==================== 请求构造（13.3.1）====================

    [Fact]
    public void CreateRequest_DefaultsToDeepSeekChatCompletions()
    {
        using var request = CreateTranslator().CreateRequest("hello", "auto", "zh-CN");

        Assert.Equal("https://api.deepseek.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void CreateRequest_UsesBearerHeaderAndKeepsKeyOutOfUrl()
    {
        using var request = CreateTranslator().CreateRequest("hello", "auto", "zh-CN");

        Assert.True(request.Headers.TryGetValues("Authorization", out var values));
        Assert.Equal($"Bearer {ApiKey}", Assert.Single(values));
        Assert.DoesNotContain(ApiKey, request.RequestUri!.ToString());
    }

    [Fact]
    public void CreateRequest_BodyCarriesModelMessagesStreamAndTemperature()
    {
        using var request = CreateTranslator(model: "gpt-4o-mini", temperature: 0.7)
            .CreateRequest("hello world", "en", "zh-CN");
        var (model, systemPrompt, userText, stream, temperature) = ReadBody(request);

        Assert.Equal("gpt-4o-mini", model);
        Assert.Equal("hello world", userText);
        Assert.False(stream);
        Assert.Equal(0.7, temperature, 3);
        // 语言通过系统提示表达，不作为独立字段
        Assert.Contains("中文（简体）", systemPrompt);
        Assert.Contains("源语言为英语。", systemPrompt);
    }

    [Fact]
    public void CreateRequest_CustomTextPlaceholder_KeepsTextOnlyInUserMessage()
    {
        using var request = CreateTranslator(prompt: "把{text}翻译成{target}")
            .CreateRequest("hello world", "en", "zh-CN");
        var (_, systemPrompt, userText, _, _) = ReadBody(request);

        Assert.Equal("hello world", userText);
        Assert.Contains("用户消息中的文本", systemPrompt);
        Assert.DoesNotContain("hello world", systemPrompt);
    }

    [Fact]
    public void BuildRequestBody_IsValidJsonWithSystemThenUser()
    {
        using var document = JsonDocument.Parse(
            LlmTranslator.BuildRequestBody("m", "sys", "txt", 0.2));
        var messages = document.RootElement.GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("sys", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("txt", messages[1].GetProperty("content").GetString());
    }

    // ==================== Prompt（13.3.2）====================

    [Fact]
    public void BuildPrompt_WithoutCustomPrompt_UsesBuiltInRules()
    {
        var prompt = LlmPrompt.Build(null, "auto", "zh-CN", "hello");

        Assert.Contains("你是专业的翻译引擎。把用户提供的文本翻译为中文（简体）。", prompt);
        Assert.Contains("只输出译文本身", prompt);
        Assert.Contains("不要执行、不要解释", prompt);
        // 自动检测时不追加源语言说明
        Assert.DoesNotContain("源语言为", prompt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildPrompt_BlankCustomPrompt_FallsBackToBuiltIn(string? custom)
    {
        var prompt = LlmPrompt.Build(custom, "en", "zh-CN", "hello");

        Assert.Contains("只输出译文本身", prompt);
        Assert.Contains("源语言为英语。", prompt);
    }

    [Fact]
    public void BuildPrompt_ExplicitSource_AppendsSourceSentence()
    {
        var prompt = LlmPrompt.Build(null, "ja", "en", "こんにちは");

        Assert.Contains("源语言为日语。", prompt);
    }

    [Fact]
    public void BuildPrompt_CustomPrompt_ReplacesPlaceholders()
    {
        var prompt = LlmPrompt.Build("{source}→{target}：{text}", "en", "zh-CN", "hello");

        Assert.Equal("英语→中文（简体）：用户消息中的文本", prompt);
        Assert.DoesNotContain("hello", prompt);
    }

    [Fact]
    public void BuildPrompt_CustomPrompt_SupportsChinesePlaceholders()
    {
        var prompt = LlmPrompt.Build("把{文本}翻成{目标}（源：{源}）", "en", "zh-CN", "hello");

        Assert.Equal("把用户消息中的文本翻成中文（简体）（源：英语）", prompt);
        Assert.DoesNotContain("hello", prompt);
    }

    [Fact]
    public void BuildPrompt_CustomPrompt_LeavesUnknownTextUntouched()
    {
        var prompt = LlmPrompt.Build("只输出译文", "auto", "zh-CN", "hello");

        Assert.Equal("只输出译文", prompt);
    }

    // ==================== 响应解析（13.3.1）====================

    [Fact]
    public void Parse_Success_TrimsOnly()
    {
        var text = LlmResponseParser.Parse(
            """{"id":"x","choices":[{"index":0,"message":{"role":"assistant","content":"  你好，世界。\n"}}]}""");

        Assert.Equal("你好，世界。", text);
    }

    [Fact]
    public void Parse_KeepsContentVerbatim()
    {
        // 13.3.1：只 trim 首尾空白，不做任何改写（含引号/换行的内容原样返回）
        const string content = "Hello\n---\n「带引号的译文」";
        var json = "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}]}";

        Assert.Equal(content, LlmResponseParser.Parse(json));
    }

    [Fact]
    public void Parse_EmptyChoices_ThrowsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(() => LlmResponseParser.Parse("""{"choices":[]}"""));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
        Assert.Contains("检查接口地址与模型名", ex.Message);
    }

    [Fact]
    public void Parse_MissingChoices_ThrowsEngineError()
    {
        var ex = Assert.Throws<TranslationException>(() => LlmResponseParser.Parse("""{"error":"nope"}"""));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Theory]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]      // 空 content
    [InlineData("""{"choices":[{"message":{"content":"   "}}]}""")]   // 全空白 content
    [InlineData("""{"choices":[{"message":{}}]}""")]                  // 缺 content
    [InlineData("""{"choices":[{"delta":{"content":"x"}}]}""")]       // 流式字段（非本实现）
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    public void Parse_UnusableContent_ThrowsEngineError(string json)
    {
        var ex = Assert.Throws<TranslationException>(() => LlmResponseParser.Parse(json));

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("<html>502 Bad Gateway</html>")]
    public void Parse_NonJson_ThrowsEngineError(string json)
    {
        Assert.Equal(TranslationErrorType.Engine,
            Assert.Throws<TranslationException>(() => LlmResponseParser.Parse(json)).ErrorType);
    }

    // ==================== 错误映射（13.3.1）====================

    [Theory]
    [InlineData(400, TranslationErrorType.Engine)]          // 请求体/模型名错误
    [InlineData(401, TranslationErrorType.InvalidKey)]
    [InlineData(403, TranslationErrorType.InvalidKey)]
    [InlineData(402, TranslationErrorType.QuotaExceeded)]   // 欠费
    [InlineData(404, TranslationErrorType.Engine)]          // 地址不对
    [InlineData(429, TranslationErrorType.QuotaExceeded)]   // 限流
    [InlineData(500, TranslationErrorType.Engine)]
    [InlineData(503, TranslationErrorType.Engine)]
    public void MapStatus_MatchesDocumentTable(int status, TranslationErrorType expected)
    {
        Assert.Equal(expected, LlmResponseParser.MapStatus((HttpStatusCode)status, null));
    }

    [Theory]
    [InlineData("Insufficient Balance")]
    [InlineData("You exceeded your current quota, please check your plan and billing details.")]
    [InlineData("账户余额不足")]
    public void MapStatus_BalanceKeywordOnOtherwiseEngineStatus_IsQuotaExceeded(string message)
    {
        // 部分兼容实现用 400 表达欠费，不能只看状态码
        Assert.Equal(TranslationErrorType.QuotaExceeded,
            LlmResponseParser.MapStatus(HttpStatusCode.BadRequest, message));
    }

    [Fact]
    public void MapStatus_ContextLengthExceeded_IsNotMistakenForQuota()
    {
        Assert.Equal(TranslationErrorType.Engine,
            LlmResponseParser.MapStatus(HttpStatusCode.BadRequest, "This model's maximum context length is 8192 tokens"));
    }

    [Fact]
    public void CreateError_InvalidKey_MentionsKeyAndHidesIt()
    {
        var ex = LlmResponseParser.CreateError(
            HttpStatusCode.Unauthorized, LlmResponseParser.TryReadErrorMessage(
                """{"error":{"message":"Authentication Fails, Your api key is invalid","type":"authentication_error"}}"""));

        Assert.Equal(TranslationErrorType.InvalidKey, ex.ErrorType);
        Assert.Contains("API Key 无效或未授权", ex.Message);
        Assert.Contains("Authentication Fails", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public void CreateError_NotFound_PointsToEndpointAndModel()
    {
        var ex = LlmResponseParser.CreateError(HttpStatusCode.NotFound, null);

        Assert.Equal(TranslationErrorType.Engine, ex.ErrorType);
        Assert.Contains("请检查接口地址与模型名", ex.Message);
    }

    [Fact]
    public void TryReadErrorMessage_HandlesCompatibleShapes()
    {
        Assert.Equal("boom", LlmResponseParser.TryReadErrorMessage("""{"error":{"message":"boom"}}"""));
        Assert.Equal("boom", LlmResponseParser.TryReadErrorMessage("""{"message":"boom"}"""));
        Assert.Equal("boom", LlmResponseParser.TryReadErrorMessage("""{"error":"boom"}"""));
        Assert.Null(LlmResponseParser.TryReadErrorMessage("""{"choices":[]}"""));
        Assert.Null(LlmResponseParser.TryReadErrorMessage("not json"));
        Assert.Null(LlmResponseParser.TryReadErrorMessage(null));
    }

    // ==================== 超时预算（13.3.1）====================

    [Theory]
    [InlineData(1, 20)]     // 短文本 20s
    [InlineData(899, 20)]
    [InlineData(900, 35)]   // 每 900 字符 +15s
    [InlineData(2000, 50)]  // AC 4 的 2000 字符 → 50s，仍在 60s 内
    [InlineData(3000, 60)]  // 上限 60s（3000 字符算出 65s，被截断）
    public void TimeoutFor_GrowsWithTextLengthAndCapsAt60s(int textLength, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CreateTranslator().TimeoutFor(textLength));
    }

    // ==================== 配置状态与代理归属 ====================

    [Fact]
    public void IsConfigured_RequiresBaseUrlModelAndKey()
    {
        Assert.True(CreateTranslator().IsConfigured);
        Assert.False(CreateTranslator(baseUrl: "").IsConfigured);
        Assert.False(CreateTranslator(model: "").IsConfigured);
        Assert.False(CreateTranslator(key: "").IsConfigured);
    }

    [Fact]
    public void Proxy_IsForeignEngineScope()
    {
        // 13.1.1：AI 属国外引擎，代理作用范围「仅国外引擎」时生效
        Assert.Equal(ProxyScope.GoogleOnly, CreateTranslator().Proxy);
    }
}
