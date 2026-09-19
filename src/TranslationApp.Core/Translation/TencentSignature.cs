using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TranslationApp.Core.Translation;

/// <summary>签名结果：Authorization 头值 + 便于排查的中间量（SecretId 不放入，避免误记日志）。</summary>
public sealed record TencentSignatureResult(string Date, long UnixSeconds, string Signature, string Authorization);

/// <summary>
/// 腾讯云 TC3-HMAC-SHA256 签名（13.1.2）。
/// 独立成类的理由：签名细节（十六进制大小写、HMAC 字节链、参与签名的头集合、UTC 时间）
/// 任一处写错都只表现为 AuthFailure.SignatureFailure，靠真实请求排查成本很高，
/// 拆出来才能用固定输入/固定期望值做向量单测。
/// </summary>
public static class TencentSignature
{
    public const string Algorithm = "TC3-HMAC-SHA256";

    /// <summary>参与签名的请求头集合：只有 content-type 与 host，X-TC-* 不参与（13.1.2）。</summary>
    public const string SignedHeaders = "content-type;host";

    /// <summary>
    /// 计算签名并组装 Authorization 头值。
    /// </summary>
    /// <param name="contentType">参与签名的 Content-Type，必须与真实发送的头逐字符一致。</param>
    /// <param name="payload">请求体原文；必须与真实发送的字节完全一致（否则摘要不匹配）。</param>
    /// <param name="timestamp">请求时间：date 与 timestamp 一律取 UTC，本地时区会导致签名过期。</param>
    public static TencentSignatureResult Compute(
        string secretId,
        string secretKey,
        string service,
        string host,
        string contentType,
        string payload,
        DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var unixSeconds = timestamp.ToUnixTimeSeconds();

        // 规范请求串：方法 / 规范 URI / 规范查询串（空） / 规范头（各以 \n 结尾 + 一个空行） / 签名头列表 / 请求体摘要
        var canonicalRequest = string.Join(
            '\n',
            "POST",
            "/",
            "",
            $"content-type:{contentType}",
            $"host:{host}",
            "",
            SignedHeaders,
            Sha256Hex(payload));

        var credentialScope = $"{date}/{service}/tc3_request";
        var stringToSign = string.Join(
            '\n',
            Algorithm,
            unixSeconds.ToString(CultureInfo.InvariantCulture),
            credentialScope,
            Sha256Hex(canonicalRequest));

        // 派生密钥：每一级 HMAC 的输出都是「字节」，直接作为下一级的 key
        // （转成十六进制字符串再用会得到完全错误的签名）
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var kService = HmacSha256(kDate, service);
        var kSigning = HmacSha256(kService, "tc3_request");
        var signature = Convert.ToHexStringLower(HmacSha256(kSigning, stringToSign));

        var authorization = $"{Algorithm} Credential={secretId}/{credentialScope}, " +
                            $"SignedHeaders={SignedHeaders}, Signature={signature}";

        return new TencentSignatureResult(date, unixSeconds, signature, authorization);
    }

    /// <summary>SHA256 摘要的小写十六进制（腾讯要求全小写）。</summary>
    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static byte[] HmacSha256(byte[] key, string data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
