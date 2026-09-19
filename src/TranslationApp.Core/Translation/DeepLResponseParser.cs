using System.Net;
using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// DeepL 响应解析器（13.1.5）。
/// 成功：<c>{"translations":[{"detected_source_language":"EN","text":"你好"}]}</c>
/// 失败：错误用 HTTP 状态码表达，正文形如 <c>{"message":"..."}</c>（实测无凭据时 403 + message）。
/// </summary>
public static class DeepLResponseParser
{
    /// <summary>DeepL 用非标准状态码 456 表示「额度用尽」。</summary>
    internal const int QuotaExceededStatus = 456;

    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, "DeepL 返回空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "DeepL 响应格式异常", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "DeepL 响应缺少译文");
            }

            var first = translations[0];
            var text = first.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
            if (string.IsNullOrEmpty(text))
            {
                throw new TranslationException(TranslationErrorType.Engine, "DeepL 返回空译文");
            }

            // 省略 source_lang 时 DeepL 自行检测并回传 detected_source_language（13.1.5 的更正）
            var detected = first.TryGetProperty("detected_source_language", out var detectedElement)
                && detectedElement.ValueKind == JsonValueKind.String
                ? detectedElement.GetString()
                : null;

            return (text, detected);
        }
    }

    /// <summary>取错误正文里的 message（取不到返回 null）。</summary>
    public static string? TryReadMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 状态码归类（13.1.5）：400 → Engine；403 → InvalidKey；456 / 429 → QuotaExceeded；
    /// 413 → Engine（请求过大，实现已分块）；5xx → Engine。
    /// </summary>
    internal static TranslationErrorType MapStatus(HttpStatusCode status) => (int)status switch
    {
        400 or 413 => TranslationErrorType.Engine,
        401 or 403 => TranslationErrorType.InvalidKey,
        QuotaExceededStatus or 429 => TranslationErrorType.QuotaExceeded,
        _ => TranslationErrorType.Engine,
    };

    /// <summary>构造带分类与用户可读文案的异常（文案不含 Key）。</summary>
    internal static TranslationException CreateError(HttpStatusCode status, string? message)
    {
        var errorType = MapStatus(status);
        var detail = string.IsNullOrWhiteSpace(message) ? "" : $"（{message}）";

        var text = errorType switch
        {
            TranslationErrorType.InvalidKey =>
                $"DeepL 密钥无效或未授权{detail}，请检查 Authentication Key（HTTP {(int)status}）",
            TranslationErrorType.QuotaExceeded =>
                $"DeepL 额度用尽或触发限流{detail}，请检查账号用量（HTTP {(int)status}）",
            _ => $"DeepL 引擎异常{detail}（HTTP {(int)status}）",
        };

        return new TranslationException(errorType, text);
    }
}
