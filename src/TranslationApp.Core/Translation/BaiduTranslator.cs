using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 百度翻译（标准版通用文本翻译，13.1.3）：POST 表单 + MD5 签名，无请求头鉴权。
/// 签名 sign = MD5(appid + q + salt + 密钥)，严格按此顺序直接拼接、无分隔符，
/// 输出 32 位小写十六进制；参与签名的 q 是**未 URL 编码的原始文本**（编码由表单层完成）。
/// 统一用 POST：GET 会把长文本与密钥带进 URL/日志。
/// </summary>
public sealed class BaiduTranslator : OfficialTranslatorBase
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    /// <summary>单次请求文本上限（13.1.3：百度约 6000 字节，按 900 字符分块留足余量）。</summary>
    private const int MaxTextLength = 900;

    private const string SaltChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public BaiduTranslator(AppSettings settings, HttpClientProvider httpProvider)
        : base(settings, httpProvider)
    {
    }

    public override string Id => "baidu";

    public override string Name => "百度翻译";

    protected override int MaxChunkLength => MaxTextLength;

    /// <summary>百度国内可达，仅「全部引擎」模式下走代理（13.1.1）。</summary>
    protected internal override ProxyScope Proxy => ProxyScope.All;

    public override bool IsConfigured => HasSecret(Settings.BaiduAppId, Settings.BaiduAppKeyEncrypted);

    /// <summary>百度语言码（13.1.7）：jp/kor/fra/spa 为百度特有缩写，不能直接透传内部码。</summary>
    protected override string? ToEngineLanguage(string internalCode) => ToBaiduCode(internalCode);

    /// <summary>内部语言码 → 百度语言码；返回 null 表示百度不支持该语言（调用方据此明确报错）。</summary>
    internal static string? ToBaiduCode(string internalCode) => internalCode switch
    {
        TranslationLanguages.AutoCode => "auto",
        "zh-CN" => "zh",
        "zh-TW" => "cht",
        "ja" => "jp",
        "ko" => "kor",
        "fr" => "fra",
        "es" => "spa",
        "en" or "de" or "ru" or "it" or "pt" => internalCode,
        _ => null,
    };

    /// <summary>百度语言码 → 内部语言码（自动检测时服务端回传真实语言）。</summary>
    internal static string FromBaiduCode(string code) => code switch
    {
        "zh" => "zh-CN",
        "cht" => "zh-TW",
        "jp" => "ja",
        "kor" => "ko",
        "fra" => "fr",
        "spa" => "es",
        _ => code,
    };

    /// <summary>计算签名（internal 供固定向量单测：拼接顺序或大小写写错只会表现为 54001）。</summary>
    internal static string Sign(string appId, string text, string salt, string secret) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(appId + text + salt + secret)));

    /// <summary>salt 为 8~16 位随机字母数字（13.1.3），仅用于让签名每次不同，防重放。</summary>
    internal static string CreateSalt()
    {
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = SaltChars[Random.Shared.Next(SaltChars.Length)];
        }

        return new string(chars);
    }

    protected override async Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken)
    {
        var appId = Settings.BaiduAppId;
        var secret = ReadSecret(Settings.BaiduAppKeyEncrypted);
        var salt = CreateSalt();

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = engineSource,
                ["to"] = engineTarget,
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = Sign(appId, text, salt, secret),
            }),
        };

        var (isSuccess, status, body) = await SendAsync(request, text.Length, cancellationToken);
        if (!isSuccess)
        {
            throw FromHttpStatus(status);
        }

        var (translated, detected) = BaiduResponseParser.Parse(body);
        return (translated, detected is null ? null : FromBaiduCode(detected));
    }
}
