using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 微软 Azure Translator（翻译工具 V3，13.1.4）：纯 REST + JSON，零签名，实现难度低。
/// 鉴权走请求头 Ocp-Apim-Subscription-Key（有 Region 时另加 Ocp-Apim-Subscription-Region）；
/// 请求体是 JSON 数组 <c>[{"Text":"hello"}]</c>；
/// **自动检测 = 省略 from 参数**（不要传 from=auto）。
/// 域名可达性已实测（2026-09-13：HTTP 401，见 build/probe-engines.ps1）。
/// </summary>
public sealed class AzureTranslator : OfficialTranslatorBase
{
    /// <summary>V3 翻译端点（设置页的端点可达性探测与提示也要用，故为 public）。</summary>
    public const string Endpoint = "https://api.cognitive.microsofttranslator.com/translate";

    public const string ApiVersion = "3.0";

    /// <summary>与腾讯/百度保持同一分块粒度，便于共用超时预算模型（13.1.4 未规定单次上限）。</summary>
    private const int MaxTextLength = 900;

    /// <summary>Content-Type 与 13.1.4 一致（charset 大小写不影响解析，但保持与文档一致）。</summary>
    private const string ContentType = "application/json; charset=UTF-8";

    public AzureTranslator(AppSettings settings, HttpClientProvider httpProvider)
        : base(settings, httpProvider)
    {
    }

    public override string Id => "azure";

    public override string Name => "Azure Translator";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>国外引擎：在「仅国外引擎」作用域下即走代理（13.1.1）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.GoogleOnly;

    /// <summary>Azure 只有一个必填的密钥字段，没有独立的明文标识（13.1.1 的 IsConfigured 判定）。</summary>
    public override bool IsConfigured => !string.IsNullOrWhiteSpace(ReadSecret(Settings.AzureSubscriptionKeyEncrypted));

    /// <summary>Azure 语言码（13.1.7）。</summary>
    protected override string? ToEngineLanguage(string internalCode) => ToAzureCode(internalCode);

    /// <summary>内部语言码 → Azure 语言码；返回 null 表示 Azure 不支持该语言。</summary>
    internal static string? ToAzureCode(string internalCode) => internalCode switch
    {
        TranslationLanguages.AutoCode => "auto",
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        "en" or "ja" or "ko" or "fr" or "de" or "ru" or "es" or "it" or "pt" => internalCode,
        _ => null,
    };

    /// <summary>Azure 语言码 → 内部语言码（自动检测时 detectedLanguage 回填真实语言）。</summary>
    internal static string FromAzureCode(string code) => code switch
    {
        "zh-Hans" => "zh-CN",
        "zh-Hant" => "zh-TW",
        _ => code,
    };

    /// <summary>
    /// 构造请求 URI。自动检测通过**省略 from 参数**表达（13.1.4）；
    /// 未经实测确认 from=auto 是否被接受，故不采用。
    /// </summary>
    internal static Uri BuildRequestUri(string engineSource, string engineTarget)
    {
        var url = new StringBuilder(Endpoint)
            .Append("?api-version=").Append(ApiVersion)
            .Append("&to=").Append(Uri.EscapeDataString(engineTarget));

        if (!string.Equals(engineSource, "auto", StringComparison.Ordinal))
        {
            url.Append("&from=").Append(Uri.EscapeDataString(engineSource));
        }

        return new Uri(url.ToString());
    }

    /// <summary>请求体：JSON 数组，只发 1 个元素（13.1.4）。</summary>
    internal static string BuildRequestBody(string text) =>
        JsonSerializer.Serialize(new[] { new TranslateInput(text) });

    /// <summary>构造完整请求（internal 供单测断言头与 URI，不实际发送）。</summary>
    internal HttpRequestMessage CreateRequest(string text, string engineSource, string engineTarget)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(BuildRequestBody(text)));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);

        var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(engineSource, engineTarget))
        {
            Content = content,
        };

        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", ReadSecret(Settings.AzureSubscriptionKeyEncrypted));

        // 多服务/区域化资源必须带 Region；单服务资源留空时不发该头（13.1.4）
        var region = Settings.AzureRegion?.Trim();
        if (!string.IsNullOrEmpty(region))
        {
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", region);
        }

        return request;
    }

    protected override async Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken)
    {
        var (isSuccess, status, body) = await SendAsync(
            CreateRequest(text, engineSource, engineTarget), text.Length, cancellationToken);

        if (!isSuccess)
        {
            // 错误归类以 HTTP 状态码为主、error.code 为辅（13.1.4）
            var (code, message) = AzureResponseParser.TryReadError(body);
            throw AzureResponseParser.CreateError(status, code, message);
        }

        var (translated, detected) = AzureResponseParser.Parse(body);
        return (translated, detected is null ? null : FromAzureCode(detected));
    }

    /// <summary>请求体元素结构（属性名必须为 Text，13.1.4）。</summary>
    private sealed record TranslateInput(string Text);
}
