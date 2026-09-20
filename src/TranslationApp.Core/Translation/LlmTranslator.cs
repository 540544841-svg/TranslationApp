using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// AI 翻译通道（FR-022 / 13.3.1）：OpenAI 兼容的 <c>POST {base}/chat/completions</c>。
/// 与四家官方引擎同构（走 <see cref="OfficialTranslatorBase"/>：分块、超时、网络重试 1 次、取消），差异点：
/// <list type="bullet">
/// <item>接口地址由用户填写，需归一化（<see cref="NormalizeEndpoint"/>）；</item>
/// <item>超时预算更宽松（短 20s / 每 900 字符 +15s / 上限 60s，覆写 <see cref="TimeoutFor"/>）；</item>
/// <item>不回传检测语言，源语言保持「自动检测」（<c>DetectedSourceLanguage</c> 恒为 null，与其它引擎的行为差异）；</item>
/// <item>错误映射见 <see cref="LlmResponseParser"/>（404 / 400 提示检查 BaseURL 与模型名）。</item>
/// </list>
/// 默认不启用：BaseURL / Key / 模型名三者齐备才算「已配置」（13.3.1），未配置时设置页显示「未配置」且不可选。
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

    public override string Name => "AI 翻译（OpenAI 兼容）";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>国外引擎：在「仅国外引擎」作用域下即走代理（13.1.1 / 13.5.1 的代理文案）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.GoogleOnly;

    /// <summary>BaseURL / API Key / 模型名三者均非空才算已配置（13.3.1）。</summary>
    public override bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Settings.LlmBaseUrl)
        && !string.IsNullOrWhiteSpace(Settings.LlmModel)
        && !string.IsNullOrWhiteSpace(ReadSecret(Settings.LlmApiKeyEncrypted));

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

    /// <summary>
    /// BaseURL 归一化（13.3.1）：以 /chat/completions 结尾 → 原样；以 /v1 结尾 → 追加路径；
    /// 其他 → 追加 /v1/chat/completions。末尾斜杠先去掉，避免拼出 //chat/completions。
    /// </summary>
    public static string NormalizeEndpoint(string? baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return "";
        }

        if (url.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? url + ChatCompletionsPath
            : url + "/v1" + ChatCompletionsPath;
    }

    /// <summary>
    /// 请求体（13.3.1）：<c>{"model":...,"messages":[system,user],"stream":false,"temperature":0.2}</c>。
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
        var apiKey = ReadSecret(Settings.LlmApiKeyEncrypted);
        var url = NormalizeEndpoint(Settings.LlmBaseUrl);
        var body = BuildRequestBody(Settings.LlmModel, systemPrompt, text, Settings.LlmTemperature);

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
        };

        // Key 只放请求头，绝不进 URL 或日志（13.3.1 / FR-010）
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        return request;
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

        return TranslationApp.Core.Dictionary.AiDictionaryParser.Parse(LlmResponseParser.Parse(body));
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
        return (LlmResponseParser.Parse(body), null);
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
