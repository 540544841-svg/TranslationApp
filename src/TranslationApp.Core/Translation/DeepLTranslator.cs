using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// DeepL（13.1.5）：无签名，纯 REST + JSON。
/// 鉴权走请求头 <c>Authorization: DeepL-Auth-Key {key}</c>（**不用 URL/表单 auth_key**，避免密钥进 URL 与日志）；
/// 端点按 Key 是否以 ":fx" 结尾自动判定免费/付费（设置字段仅作人工覆盖）；
/// <c>target_lang</c> 必填且大写；
/// 源语言为 auto 时**省略 source_lang**，由 DeepL 自行检测并回传 detected_source_language。
/// 两个端点可达性已实测（2026-09-13：HTTP 403，见 build/probe-engines.ps1）。
/// </summary>
public sealed class DeepLTranslator : OfficialTranslatorBase
{
    /// <summary>免费版端点（设置页的端点可达性探测与提示也要用，故为 public）。</summary>
    public const string FreeEndpointUrl = "https://api-free.deepl.com/v2/translate";

    /// <summary>付费版端点。</summary>
    public const string PaidEndpointUrl = "https://api.deepl.com/v2/translate";

    /// <summary>免费版 Key 的后缀（官方约定）。</summary>
    internal const string FreeKeySuffix = ":fx";

    /// <summary>与腾讯/百度保持同一分块粒度（13.1.5 未规定单次上限，413 表示请求过大且已分块）。</summary>
    private const int MaxTextLength = 900;

    private const string ContentType = "application/json; charset=UTF-8";

    /// <summary>表示「省略 source_lang」的内部标记（与 TranslationLanguages.AutoCode 同值语义）。</summary>
    private const string Auto = TranslationLanguages.AutoCode;

    public DeepLTranslator(AppSettings settings, HttpClientProvider httpProvider)
        : base(settings, httpProvider)
    {
    }

    public override string Id => "deepl";

    public override string Name => "DeepL";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>国外引擎：在「仅国外引擎」作用域下即走代理（13.1.1）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.GoogleOnly;

    /// <summary>DeepL 只有一个必填的密钥字段，没有独立的明文标识。</summary>
    public override bool IsConfigured => !string.IsNullOrWhiteSpace(ReadSecret(Settings.DeepLApiKeyEncrypted));

    /// <summary>DeepL 语言码（13.1.7）：全部大写。</summary>
    protected override string? ToEngineLanguage(string internalCode) => ToDeepLCode(internalCode);

    /// <summary>内部语言码 → DeepL 语言码（大写）；返回 null 表示 DeepL 不支持该语言。</summary>
    internal static string? ToDeepLCode(string internalCode) => internalCode switch
    {
        Auto => Auto,
        "zh-CN" => "ZH",
        "zh-TW" => "ZH-HANT", // 繁体目标码是否受支持需实测（13.9 第 7 项）
        "en" or "ja" or "ko" or "fr" or "de" or "ru" or "es" or "it" or "pt" => internalCode.ToUpperInvariant(),
        _ => null,
    };

    /// <summary>DeepL 语言码（大写）→ 内部语言码。</summary>
    internal static string FromDeepLCode(string code) => code.ToUpperInvariant() switch
    {
        "ZH" or "ZH-HANS" => "zh-CN",
        "ZH-HANT" => "zh-TW",
        _ => code.ToLowerInvariant(),
    };

    /// <summary>Key 是否指向免费端点（以 ":fx" 结尾，大小写不敏感）。</summary>
    public static bool KeyUsesFreeEndpoint(string? apiKey) =>
        apiKey?.TrimEnd().EndsWith(FreeKeySuffix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 端点选择（13.1.5）：Key 以 ":fx" 结尾 → 免费端点（官方约定，优先）；
    /// 否则按 <paramref name="useFreeEndpoint"/> 判定 —— 该值默认 true，且设置页会在输入 Key 时
    /// 按后缀自动同步（后缀不是 :fx 时自动置 false），因此常态即为「按后缀自动判定」；
    /// 用户手动切换开关后即为「人工覆盖」。
    /// </summary>
    internal static string ResolveEndpointUrl(string? apiKey, bool useFreeEndpoint) =>
        KeyUsesFreeEndpoint(apiKey) || useFreeEndpoint ? FreeEndpointUrl : PaidEndpointUrl;

    /// <summary>
    /// 请求体（13.1.5）：<c>{"text":["hello"],"target_lang":"ZH"}</c>；
    /// 源语言为 auto 时省略 source_lang（不传 auto）。
    /// </summary>
    internal static string BuildRequestBody(string text, string engineSource, string engineTarget)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = new[] { text },
            ["target_lang"] = engineTarget,
        };

        if (!string.Equals(engineSource, Auto, StringComparison.Ordinal))
        {
            body["source_lang"] = engineSource;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>构造完整请求（internal 供单测断言头、端点与请求体，不实际发送）。</summary>
    internal HttpRequestMessage CreateRequest(string text, string engineSource, string engineTarget)
    {
        var apiKey = ReadSecret(Settings.DeepLApiKeyEncrypted);
        var url = ResolveEndpointUrl(apiKey, Settings.DeepLUseFreeEndpoint);

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(BuildRequestBody(text, engineSource, engineTarget)));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");
        return request;
    }

    protected override async Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken)
    {
        var (isSuccess, status, body) = await SendAsync(
            CreateRequest(text, engineSource, engineTarget), text.Length, cancellationToken);

        if (!isSuccess)
        {
            // 错误用 HTTP 状态码表达（13.1.5）；正文 message 仅作排障细节
            throw DeepLResponseParser.CreateError(status, DeepLResponseParser.TryReadMessage(body));
        }

        var (translated, detected) = DeepLResponseParser.Parse(body);
        return (translated, detected is null ? null : FromDeepLCode(detected));
    }
}
