using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// AI 翻译通道（FR-022 / 13.3.1）：一条**通用**的 OpenAI 风格接口，按供应商档配置。
/// 与四家官方引擎同构（走 <see cref="OfficialTranslatorBase"/>：分块、超时、网络重试 1 次、取消），差异点：
/// <list type="bullet">
/// <item>接口地址、密钥、模型、协议（<see cref="LlmWireApi"/>）都按「当前供应商档」取，可多档切换；</item>
/// <item>支持两种 wire API：<c>/chat/completions</c>（messages）与 <c>/responses</c>（instructions + input）；</item>
/// <item>鉴权可关（本地 Ollama / vLLM / 内网网关），另可附加自定义请求头；</item>
/// <item>超时预算更宽松（短 20s / 每 900 字符 +15s / 上限 60s，覆写 <see cref="TimeoutFor"/>）；</item>
/// <item>不回传检测语言，源语言保持「自动检测」（<c>DetectedSourceLanguage</c> 恒为 null）；</item>
/// <item>错误映射见 <see cref="LlmResponseParser"/>（404 / 400 提示检查 BaseURL 与模型名）。</item>
/// </list>
/// 默认不启用：当前档的 接口地址 / 模型名（需要鉴权时还有 Key）齐备才算「已配置」（13.3.1）。
/// </summary>
public sealed class LlmTranslator : OfficialTranslatorBase, IPromptDirectiveTranslator
{
    /// <summary>13.7 默认接口地址（DeepSeek）。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>13.7 默认模型名。</summary>
    public const string DefaultModel = "deepseek-chat";

    /// <summary>13.7 默认采样温度。</summary>
    public const double DefaultTemperature = 0.2;

    /// <summary>OpenAI 兼容的对话补全路径。</summary>
    internal const string ChatCompletionsPath = "/chat/completions";

    /// <summary>OpenAI Responses API 路径。</summary>
    internal const string ResponsesPath = "/responses";

    /// <summary>模型列表路径（OpenAI 兼容的 GET /v1/models）。</summary>
    internal const string ModelsPath = "/models";

    /// <summary>版本段：地址以它结尾时只补 endpoint，否则连同 /v1 一起补。</summary>
    internal const string VersionSegment = "/v1";

    /// <summary>13.3.2：输入上限沿用现有 3000 字符（与 FR-004 一致），不分块。</summary>
    private const int MaxTextLength = 3000;

    private const int ShortTimeoutSeconds = 20;
    private const int TimeoutStepChars = 900;
    private const int TimeoutStepSeconds = 15;
    private const int MaxTimeoutSeconds = 60;

    private const string ContentType = "application/json; charset=UTF-8";

    public LlmTranslator(AppSettings settings, HttpClientProvider httpProvider)
        : base(settings, httpProvider)
    {
    }

    public override string Id => "llm";

    public override string Name => "AI 翻译（通用接口）";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>国外引擎：在「仅国外引擎」作用域下即走代理（13.1.1 / 13.5.1 的代理文案）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.GoogleOnly;

    /// <summary>当前供应商档；一档都没有时为 null（界面保证至少留一档）。</summary>
    internal LlmProvider? Provider => Settings.ActiveLlmProvider;

    /// <summary>当前档使用的协议；没有档时按默认的对话补全（只影响错误提示里的措辞）。</summary>
    internal LlmWireApi WireApi => Provider?.WireApi ?? LlmWireApi.Chat;

    /// <summary>接口地址 / 模型名齐备，且（需要鉴权时）密钥可解密，才算已配置。</summary>
    public override bool IsConfigured
    {
        get
        {
            var provider = Provider;
            return provider is not null
                && !string.IsNullOrWhiteSpace(provider.BaseUrl)
                && !string.IsNullOrWhiteSpace(provider.Model)
                && (!provider.RequiresAuth || !string.IsNullOrWhiteSpace(ReadSecret(provider.ApiKeyEncrypted)));
        }
    }

    /// <summary>
    /// LLM 只认显示名（写进 Prompt），不需要各引擎语言码，内部语言码原样透传；
    /// 因此不会出现「引擎不支持该语言」（支持哪种语言由用户在 Prompt 里约定）。
    /// </summary>
    protected override string? ToEngineLanguage(string internalCode) => internalCode;

    /// <summary>
    /// 超时预算（13.3.1）：短文本 20s，每 900 字符 +15s，上限 60s。
    /// 2000 字符 → 20 + 15 × 2 = 50s，仍在 60s 内。
    /// </summary>
    protected internal override TimeSpan TimeoutFor(int textLength)
    {
        var seconds = ShortTimeoutSeconds + TimeoutStepSeconds * (textLength / TimeoutStepChars);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxTimeoutSeconds));
    }

    // ============================================================
    // 地址归一化
    // ============================================================

    /// <summary>
    /// 归一化接口地址：先剥掉用户可能已经写进去的 endpoint 后缀（避免拼出
    /// <c>/chat/completions/chat/completions</c>），再按协议补齐：
    /// 以 /v1 结尾 → 追加 endpoint；其它 → 追加 /v1 + endpoint。末尾斜杠先去掉。
    /// </summary>
    public static string NormalizeEndpoint(string? baseUrl, LlmWireApi wireApi = LlmWireApi.Chat)
    {
        var url = StripEndpointSuffix((baseUrl ?? "").Trim().TrimEnd('/'));
        if (url.Length == 0)
        {
            return "";
        }

        var path = wireApi == LlmWireApi.Responses ? ResponsesPath : ChatCompletionsPath;
        return url.EndsWith(VersionSegment, StringComparison.OrdinalIgnoreCase)
            ? url + path
            : url + VersionSegment + path;
    }

    /// <summary>模型列表地址（<c>GET {base}/v1/models</c>），归一化规则与 <see cref="NormalizeEndpoint"/> 一致。</summary>
    public static string ModelsEndpoint(string? baseUrl)
    {
        var url = StripEndpointSuffix((baseUrl ?? "").Trim().TrimEnd('/'));
        if (url.Length == 0)
        {
            return "";
        }

        return url.EndsWith(VersionSegment, StringComparison.OrdinalIgnoreCase)
            ? url + ModelsPath
            : url + VersionSegment + ModelsPath;
    }

    /// <summary>剥掉已写进地址里的 endpoint 后缀，回到「根地址」。</summary>
    private static string StripEndpointSuffix(string url)
    {
        if (url.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return url[..^ChatCompletionsPath.Length].TrimEnd('/');
        }

        return url.EndsWith(ResponsesPath, StringComparison.OrdinalIgnoreCase)
            ? url[..^ResponsesPath.Length].TrimEnd('/')
            : url;
    }

    // ============================================================
    // 请求构造
    // ============================================================

    /// <summary>
    /// 请求体（chat 协议）：<c>{"model":...,"messages":[system,user],"stream":false,"temperature":0.2}</c>。
    /// 源/目标语言通过系统 Prompt 表达，不作为独立字段发送。
    /// </summary>
    internal static string BuildRequestBody(string model, string systemPrompt, string text, double temperature) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = text },
            },
            ["stream"] = false,
            ["temperature"] = temperature,
        });

    /// <summary>
    /// 请求体（responses 协议）：<c>{"model":...,"instructions":...,"input":...,"temperature":...}</c>。
    /// 系统提示走 <c>instructions</c>、正文走 <c>input</c>，与官方 Responses API 的字段一致；
    /// <c>store=false</c> 明确不要在服务端留存本次请求（本产品不把用户文本留在第三方）。
    /// </summary>
    internal static string BuildResponsesRequestBody(string model, string systemPrompt, string text, double temperature) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = systemPrompt,
            ["input"] = text,
            ["stream"] = false,
            ["store"] = false,
            ["temperature"] = temperature,
        });

    /// <summary>
    /// 自定义请求头（JSON 对象）：解析失败按「没有自定义头」处理，绝不因一个笔误让整条链路报错。
    /// 需要鉴权时忽略用户自己写的 Authorization，避免和内置 Bearer 头重复。
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ParseExtraHeaders(string? json, bool hasBuiltInAuth = false)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Length == 0)
                {
                    continue;
                }

                if (hasBuiltInAuth && property.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
                headers.Add(new(property.Name, value));
            }

            return headers;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>构造完整请求（internal 供单测断言头、地址与请求体，不实际发送）。</summary>
    internal HttpRequestMessage CreateRequest(string text, string engineSource, string engineTarget) =>
        CreateRequest(text, engineSource, engineTarget, TranslationDirective.None);

    /// <summary>带语境/风格指令的请求构造（FR-050 / FR-051）：指令只进 system prompt，user 消息恒为原文本体。</summary>
    internal HttpRequestMessage CreateRequest(
        string text, string engineSource, string engineTarget, TranslationDirective directive) =>
        BuildRequest(text, LlmPrompt.Build(Settings.LlmPrompt, engineSource, engineTarget, text, directive));

    /// <summary>请求构造的公共部分：system = 传入的提示，user = 文本本体，Key 只在请求头。</summary>
    private HttpRequestMessage BuildRequest(string text, string systemPrompt)
    {
        var provider = Provider ?? throw new TranslationException(
            TranslationErrorType.InvalidKey, "尚未配置 AI 供应商：请在「设置 → 引擎」里新建一档并填好接口地址与模型名");

        var url = NormalizeEndpoint(provider.BaseUrl, provider.WireApi);
        var body = provider.WireApi == LlmWireApi.Responses
            ? BuildResponsesRequestBody(provider.Model, systemPrompt, text, Settings.LlmTemperature)
            : BuildRequestBody(provider.Model, systemPrompt, text, Settings.LlmTemperature);

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
        };

        ApplyAuth(request, provider);

        // Key 只放请求头，绝不进 URL 或日志（13.3.1 / FR-010）
        foreach (var header in ParseExtraHeaders(provider.ExtraHeadersJson, provider.RequiresAuth))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    /// <summary>按档位决定要不要带 Authorization（本地模型可以完全不带）。</summary>
    private void ApplyAuth(HttpRequestMessage request, LlmProvider provider)
    {
        if (provider.RequiresAuth)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization", $"Bearer {ReadSecret(provider.ApiKeyEncrypted)}");
        }
    }

    /// <summary>
    /// 拉取当前档的模型列表（<c>GET {base}/v1/models</c>），供设置页「获取模型列表」用。
    /// 与翻译共用同一套超时、代理与错误映射（401 → 密钥、网络不可达 → 网络）。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var provider = Provider ?? throw new TranslationException(
            TranslationErrorType.InvalidKey, "尚未配置 AI 供应商：请先新建一档并填好接口地址");
        return await ListModelsAsync(provider, cancellationToken);
    }

    /// <summary>
    /// 拉指定档的模型列表：设置页在编辑某一档（可能还不是当前档）时直接传那一档进来，不必先切档。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var url = ModelsEndpoint(provider.BaseUrl);
        if (url.Length == 0)
        {
            throw new TranslationException(TranslationErrorType.InvalidKey, "请先填写接口地址，再获取模型列表");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, provider);
        foreach (var header in ParseExtraHeaders(provider.ExtraHeadersJson, provider.RequiresAuth))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var (isSuccess, status, body) = await SendAsync(request, 0, cancellationToken);
        if (!isSuccess)
        {
            throw LlmResponseParser.CreateError(status, LlmResponseParser.TryReadErrorMessage(body));
        }

        return ParseModelIds(body);
    }

    /// <summary>
    /// 解析模型列表：认 <c>{"data":[{"id":...}]}</c>（OpenAI 兼容）、<c>{"models":[...]}</c> 与裸数组三种形态，
    /// 元素可以是字符串或带 id / name 的对象。认不出来时返回空表，由界面提示「没拿到模型」。
    /// </summary>
    internal static IReadOnlyList<string> ParseModelIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind switch
            {
                JsonValueKind.Array => root,
                JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
                JsonValueKind.Object when root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array => models,
                _ => default,
            };

            if (items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                var id = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                        => idElement.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        => nameElement.GetString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// AI 词典查询（FR-056）：同一个端点、同一套超时与错误映射，只是系统提示换成"只要 JSON 的词典"。
    /// 刻意走 <c>Unwrap</c> 后的裸引擎调用——词典答案不是译文，混进引擎看板会让 P50 与失败率失真，
    /// 也不该被术语表后置替换（那是给译文用的）。
    /// </summary>
    public async Task<TranslationApp.Core.Dictionary.AiDictionaryEntry?> QueryDictionaryAsync(
        string word, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(word, LlmPrompt.BuildDictionaryRequest(word, targetLanguage));
        var (isSuccess, status, body) = await SendAsync(request, word.Length, cancellationToken);
        if (!isSuccess)
        {
            throw LlmResponseParser.CreateError(status, LlmResponseParser.TryReadErrorMessage(body));
        }

        return TranslationApp.Core.Dictionary.AiDictionaryParser.Parse(body);
    }

    protected override Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken) =>
        RequestChunkAsync(text, engineSource, engineTarget, TranslationDirective.None, cancellationToken);

    protected override async Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, TranslationDirective directive, CancellationToken cancellationToken)
    {
        var (isSuccess, status, body) = await SendAsync(
            CreateRequest(text, engineSource, engineTarget, directive), text.Length, cancellationToken);

        if (!isSuccess)
        {
            throw LlmResponseParser.CreateError(status, LlmResponseParser.TryReadErrorMessage(body));
        }

        // 13.3.1：LLM 不回传检测结果 → 源语言保持「自动检测」，不回填（与其它引擎的行为差异）
        var translated = WireApi == LlmWireApi.Responses
            ? LlmResponseParser.ParseResponses(body)
            : LlmResponseParser.Parse(body);
        return (translated, null);
    }

    /// <summary>AI 引擎是本产品唯一支持「语境 / 换说法」指令的通道（批 5 spec §1.1）。</summary>
    public Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationDirective directive,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(text, sourceLanguage, targetLanguage, directive, cancellationToken);
}
