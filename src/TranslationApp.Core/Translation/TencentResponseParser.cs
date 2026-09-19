using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 腾讯云 TMT 响应解析器（13.1.2）。
/// 关键点：失败时 HTTP 状态码仍为 200，错误藏在 Response.Error.Code / Message 里，
/// 只看 HTTP 状态码会把「密钥错误 / 额度用尽」误判为成功。
/// 成功：{"Response":{"TargetText":"你好","Source":"en","Target":"zh","RequestId":"..."}}
/// 失败：{"Response":{"Error":{"Code":"AuthFailure.SignatureFailure","Message":"..."},"RequestId":"..."}}
/// </summary>
public static class TencentResponseParser
{
    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, "腾讯云返回空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "腾讯云响应格式异常", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Response", out var response)
                || response.ValueKind != JsonValueKind.Object)
            {
                throw new TranslationException(TranslationErrorType.Engine, "腾讯云响应结构异常");
            }

            if (response.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("Code", out var codeElement) ? codeElement.GetString() : null;
                var message = error.TryGetProperty("Message", out var messageElement) ? messageElement.GetString() : null;
                throw new TranslationException(MapErrorCode(code ?? ""), DescribeError(code ?? "", message));
            }

            var text = response.TryGetProperty("TargetText", out var textElement) ? textElement.GetString() : null;
            if (string.IsNullOrEmpty(text))
            {
                throw new TranslationException(TranslationErrorType.Engine, "腾讯云返回空译文");
            }

            var detected = response.TryGetProperty("Source", out var sourceElement) && sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString()
                : null;

            return (text, detected);
        }
    }

    /// <summary>
    /// 错误码归类（13.1.2）。未列出的错误码一律归入 Engine（不新增枚举值，13.1.1）。
    /// </summary>
    internal static TranslationErrorType MapErrorCode(string code) => code switch
    {
        "AuthFailure.SignatureFailure"
            or "AuthFailure.SignatureExpire"
            or "AuthFailure.SecretIdNotFound"
            or "AuthFailure.TokenFailure"
            or "AuthFailure.UnauthorizedOperation" => TranslationErrorType.InvalidKey,

        "FailedOperation.NoFreeAmount"
            or "FailedOperation.ServiceIsolate"
            or "LimitExceeded"
            or "RequestLimitExceeded" => TranslationErrorType.QuotaExceeded,

        _ => TranslationErrorType.Engine,
    };

    /// <summary>按错误码给出用户可读提示（13.1.2 的「用户提示」列）。</summary>
    internal static string DescribeError(string code, string? engineMessage)
    {
        var detail = string.IsNullOrWhiteSpace(engineMessage) ? "" : $"（{engineMessage}）";
        return code switch
        {
            "AuthFailure.SignatureFailure" or "AuthFailure.SignatureExpire" or "AuthFailure.SecretIdNotFound"
                or "AuthFailure.TokenFailure" or "AuthFailure.UnauthorizedOperation" =>
                $"腾讯云密钥无效或签名失败，请检查 SecretId/SecretKey 与系统时间{detail}",

            "FailedOperation.NoFreeAmount" or "FailedOperation.ServiceIsolate"
                or "LimitExceeded" or "RequestLimitExceeded" =>
                $"腾讯云额度用尽或触发限流{detail}，请稍后重试或检查控制台用量",

            "UnsupportedOperation.UnsupportedLanguage" or "UnsupportedOperation.TextTooLong" or "InvalidParameter" =>
                $"腾讯云不支持该语言方向或文本过长{detail}",

            // 文档将 InternalError 与 InternalError.* 归入同一类
            _ when code.StartsWith("InternalError", StringComparison.Ordinal) =>
                $"腾讯云引擎内部错误，请稍后重试{detail}",

            _ => $"腾讯云引擎异常{detail}（错误码 {(string.IsNullOrEmpty(code) ? "未知" : code)}）",
        };
    }
}
