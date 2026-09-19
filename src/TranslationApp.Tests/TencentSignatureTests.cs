using System.Net.Http.Headers;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 腾讯云 TC3-HMAC-SHA256 签名固定向量测试（13.1.2 / AC 5）。
/// 签名任一处写错（十六进制大小写、HMAC 字节链、参与签名的头集合、UTC 时间）都只会表现为
/// AuthFailure.SignatureFailure，靠真实请求排查成本极高，因此这里全部用固定输入与固定期望值断言。
/// 期望值由一份独立的参考实现（build/compute-signature-vectors.ps1，用 .NET 原生密码学另行实现）推算，
/// 其中「官方文档示例」的期望值同时与腾讯云文档公布的签名一致，可交叉验证算法理解无误。
/// </summary>
public class TencentSignatureTests
{
    private const string ContentType = "application/json; charset=utf-8";

    /// <summary>
    /// 腾讯云官方文档的 TC3 示例（service=cvm）。期望签名取自文档公布值，
    /// 若这里通过，说明「规范请求串 / 待签字符串 / 四级 HMAC」的整体实现与官方一致。
    /// </summary>
    [Fact]
    public void Compute_OfficialDocumentationExample_MatchesPublishedSignature()
    {
        const string payload =
            """{"Limit": 1, "Filters": [{"Values": ["\u672a\u547d\u540d"], "Name": "instance-name"}]}""";

        var result = TencentSignature.Compute(
            secretId: "AKIDz8krbsJ5yKBZQpn74WFkmLPx3*******",
            secretKey: "Gu5t9xGARNpq86cd98joQYCN3*******",
            service: "cvm",
            host: "cvm.tencentcloudapi.com",
            contentType: ContentType,
            payload: payload,
            timestamp: DateTimeOffset.FromUnixTimeSeconds(1551113065));

        Assert.Equal("2019-02-25", result.Date);
        Assert.Equal(1551113065, result.UnixSeconds);
        Assert.Equal(
            "2230eefd229f582d8b1b891af7107b91597240707d778ab3738f756258d7652c",
            result.Signature);
    }

    /// <summary>
    /// 本项目 TMT 引擎的固定向量（service=tmt，报文与 TencentTranslator 实际发送的一致）。
    /// 期望值：signature=d6338d02...b5c9，Authorization 见下。
    /// </summary>
    [Fact]
    public void Compute_TmtFixedVector_MatchesExpectedSignatureAndAuthorization()
    {
        const string payload = """{"SourceText":"hello","Source":"en","Target":"zh","ProjectId":0}""";

        var result = TencentSignature.Compute(
            secretId: "AKIDEXAMPLE1234567890",
            secretKey: "SECRETKEYEXAMPLE0987654321",
            service: "tmt",
            host: "tmt.tencentcloudapi.com",
            contentType: ContentType,
            payload: payload,
            timestamp: DateTimeOffset.FromUnixTimeSeconds(1735689600));

        Assert.Equal("2025-01-01", result.Date);
        Assert.Equal(1735689600, result.UnixSeconds);
        Assert.Equal(
            "d6338d0292e3111a5cf5384c7e5e401ac2ebdaf2ac479654df5245186132b5c9",
            result.Signature);
        Assert.Equal(
            "TC3-HMAC-SHA256 Credential=AKIDEXAMPLE1234567890/2025-01-01/tmt/tc3_request, " +
            "SignedHeaders=content-type;host, " +
            "Signature=d6338d0292e3111a5cf5384c7e5e401ac2ebdaf2ac479654df5245186132b5c9",
            result.Authorization);
    }

    /// <summary>
    /// date 与 timestamp 必须取 UTC：本机时钟若按本地时区算日期，跨零点的请求会直接签名过期。
    /// 同一时刻用不同时区表示，结果必须完全相同。
    /// </summary>
    [Fact]
    public void Compute_SameInstantInDifferentOffsets_ProducesIdenticalSignature()
    {
        const string payload = """{"SourceText":"hello","Source":"en","Target":"zh","ProjectId":0}""";
        // 2024-12-31T23:00:00Z：UTC 日期是 12-31，北京时间已是 2025-01-01
        var instant = DateTimeOffset.FromUnixTimeSeconds(1735686000);

        var asUtc = TencentSignature.Compute(
            "AKIDEXAMPLE1234567890", "SECRETKEYEXAMPLE0987654321", "tmt",
            "tmt.tencentcloudapi.com", ContentType, payload, instant.ToOffset(TimeSpan.Zero));
        var asBeijing = TencentSignature.Compute(
            "AKIDEXAMPLE1234567890", "SECRETKEYEXAMPLE0987654321", "tmt",
            "tmt.tencentcloudapi.com", ContentType, payload, instant.ToOffset(TimeSpan.FromHours(8)));

        Assert.Equal("2024-12-31", asUtc.Date);
        Assert.Equal(asUtc.Date, asBeijing.Date);
        Assert.Equal(asUtc.UnixSeconds, asBeijing.UnixSeconds);
        Assert.Equal(asUtc.Signature, asBeijing.Signature);
    }

    [Fact]
    public void Compute_DifferentPayload_ProducesDifferentSignature()
    {
        var first = TencentSignature.Compute(
            "AKIDEXAMPLE1234567890", "SECRETKEYEXAMPLE0987654321", "tmt",
            "tmt.tencentcloudapi.com", ContentType,
            """{"SourceText":"hello","Source":"en","Target":"zh","ProjectId":0}""",
            DateTimeOffset.FromUnixTimeSeconds(1735689600));
        var second = TencentSignature.Compute(
            "AKIDEXAMPLE1234567890", "SECRETKEYEXAMPLE0987654321", "tmt",
            "tmt.tencentcloudapi.com", ContentType,
            """{"SourceText":"world","Source":"en","Target":"zh","ProjectId":0}""",
            DateTimeOffset.FromUnixTimeSeconds(1735689600));

        Assert.NotEqual(first.Signature, second.Signature);
    }

    /// <summary>十六进制输出必须全小写（腾讯对大小写敏感）。</summary>
    [Fact]
    public void Compute_Signature_IsLowercaseHex64()
    {
        var result = TencentSignature.Compute(
            "AKIDEXAMPLE1234567890", "SECRETKEYEXAMPLE0987654321", "tmt",
            "tmt.tencentcloudapi.com", ContentType, "{}",
            DateTimeOffset.FromUnixTimeSeconds(1735689600));

        Assert.Equal(64, result.Signature.Length);
        Assert.All(result.Signature, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'), $"非小写十六进制字符：{c}"));
    }

    /// <summary>只签 content-type 与 host：X-TC-* 不参与，否则与官方签名不一致。</summary>
    [Fact]
    public void SignedHeaders_CoversOnlyContentTypeAndHost()
    {
        Assert.Equal("content-type;host", TencentSignature.SignedHeaders);
    }

    /// <summary>
    /// 参与签名的 Content-Type 必须与真实发送的头逐字符一致（含 "; charset=utf-8"）。
    /// 这里断言该常量能被 HttpClient 的头部类型原样解析并回写，防止签名与发送两处写法漂移。
    /// </summary>
    [Fact]
    public void ContentType_IsExactlyWhatHttpClientWouldSend()
    {
        var parsed = MediaTypeHeaderValue.Parse(TencentTranslator.ContentType);

        Assert.Equal("application/json; charset=utf-8", TencentTranslator.ContentType);
        Assert.Equal(TencentTranslator.ContentType, parsed.ToString());
    }
}
