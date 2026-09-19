using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 腾讯云机器翻译 TMT（13.1.2）：POST + TC3-HMAC-SHA256 签名。
/// 不使用官方 SDK（TencentCloudSDK.Tmt 会带来 TencentCloud.Common、Newtonsoft.Json 等程序集，
/// 对单文件体积与依赖面不利），按文档手写约 80 行签名逻辑。
/// </summary>
public sealed class TencentTranslator : OfficialTranslatorBase
{
    private const string Endpoint = "https://tmt.tencentcloudapi.com/";
    private const string Host = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string ActionName = "TextTranslate";
    private const string Version = "2018-03-21";

    /// <summary>
    /// 参与签名的 Content-Type。必须与真实发送的头逐字符一致（含 "; charset=utf-8"），
    /// 因此签名与发送共用同一常量，并由单元测试断言它恰好能被 HttpClient 原样发出。
    /// </summary>
    internal const string ContentType = "application/json; charset=utf-8";

    /// <summary>单次请求文本上限（13.1.2：腾讯单次上限需实测，按 900 字符分块不受影响）。</summary>
    private const int MaxTextLength = 900;

    public TencentTranslator(AppSettings settings, HttpClientProvider httpProvider)
        : base(settings, httpProvider)
    {
    }

    public override string Id => "tencent";

    public override string Name => "腾讯云机器翻译";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>腾讯国内可达，仅「全部引擎」模式下走代理（13.1.1）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.All;

    public override bool IsConfigured => HasSecret(Settings.TencentSecretId, Settings.TencentSecretKeyEncrypted);

    /// <summary>腾讯语言码（13.1.7）：除 zh-CN 需转 zh 外，其余与内部码同名。</summary>
    protected override string? ToEngineLanguage(string internalCode) => ToTencentCode(internalCode);

    /// <summary>内部语言码 → 腾讯语言码；返回 null 表示腾讯不支持该语言（调用方据此明确报错）。</summary>
    internal static string? ToTencentCode(string internalCode) => internalCode switch
    {
        TranslationLanguages.AutoCode => "auto",
        "zh-CN" => "zh",
        "en" or "zh-TW" or "ja" or "ko" or "fr" or "de" or "ru" or "es" or "it" or "pt" => internalCode,
        _ => null,
    };

    /// <summary>腾讯语言码 → 内部语言码（自动检测时 Response.Source 会回填真实语言）。</summary>
    internal static string FromTencentCode(string code) => code switch
    {
        "zh" => "zh-CN",
        _ => code,
    };

    protected override async Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken)
    {
        var secretId = Settings.TencentSecretId;
        var secretKey = ReadSecret(Settings.TencentSecretKeyEncrypted);

        // 请求体先序列化成字符串：签名摘要与真实发送的字节都基于这一份，避免两处不一致
        var payload = JsonSerializer.Serialize(new TextTranslateRequest(text, engineSource, engineTarget, 0));
        var timestamp = DateTimeOffset.UtcNow;
        var signature = TencentSignature.Compute(
            secretId, secretKey, Service, Host, ContentType, payload, timestamp);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = CreateJsonContent(payload),
        };
        request.Headers.Host = Host;
        request.Headers.TryAddWithoutValidation("X-TC-Action", ActionName);
        request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", signature.UnixSeconds.ToString(CultureInfo.InvariantCulture));

        // 单服务资源可留空；13.9 待实测确认 X-TC-Region 是否必填，故空值时不发该头
        var region = Settings.TencentRegion?.Trim();
        if (!string.IsNullOrEmpty(region))
        {
            request.Headers.TryAddWithoutValidation("X-TC-Region", region);
        }

        request.Headers.TryAddWithoutValidation("Authorization", signature.Authorization);

        var (isSuccess, status, body) = await SendAsync(request, text.Length, cancellationToken);
        if (!isSuccess)
        {
            throw FromHttpStatus(status);
        }

        // 注意：失败时 HTTP 仍为 200，错误在 Response.Error 里
        var (translated, detected) = TencentResponseParser.Parse(body);
        return (translated, detected is null ? null : FromTencentCode(detected));
    }

    /// <summary>构造 JSON 请求体，Content-Type 用与签名完全相同的常量值。</summary>
    private static HttpContent CreateJsonContent(string payload)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
        return content;
    }

    /// <summary>请求体结构（字段顺序与 13.1.2 示例一致）。</summary>
    private sealed record TextTranslateRequest(string SourceText, string Source, string Target, int ProjectId);
}
