using System.Net;
using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// Azure Translator V3 响应解析器（13.1.4）。
/// 成功：<c>[{"detectedLanguage":{"language":"en","score":1.0},"translations":[{"text":"你好","to":"zh-Hans"}]}]</c>
/// 失败：<c>{"error":{"code":401001,"message":"..."}}</c>
/// 错误归类以 **HTTP 状态码为主、error.code 为辅**（13.1.4 明确要求）。
/// </summary>
public static class AzureResponseParser
{
    /// <summary>解析成功响应（JSON 数组，取首个元素的首条译文）。</summary>
    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, "Azure 返回空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "Azure 响应格式异常", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            // 200 也可能带 error（例如参数问题），此处兜底为 Engine
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                var (code, message) = ReadError(error);
                throw CreateError(null, code, message);
            }

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "Azure 响应结构异常");
            }

            var first = root[0];
            if (!first.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "Azure 响应缺少译文");
            }

            var text = translations[0].TryGetProperty("text", out var textElement)
                ? textElement.GetString()
                : null;
            if (string.IsNullOrEmpty(text))
            {
                throw new TranslationException(TranslationErrorType.Engine, "Azure 返回空译文");
            }

            string? detected = null;
            if (first.TryGetProperty("detectedLanguage", out var detectedLanguage)
                && detectedLanguage.ValueKind == JsonValueKind.Object
                && detectedLanguage.TryGetProperty("language", out var language)
                && language.ValueKind == JsonValueKind.String)
            {
                detected = language.GetString();
            }

            return (text, detected);
        }
    }

    /// <summary>从错误响应体里取 <c>error.code</c> / <c>error.message</c>（取不到返回 null，不抛异常）。</summary>
    public static (string? Code, string? Message) TryReadError(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                ? ReadError(error)
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Code, string? Message) ReadError(JsonElement error)
    {
        // error.code 在实测中为数字（401001），文档示例为数字；仍兼容字符串
        var code = error.TryGetProperty("code", out var codeElement)
            ? codeElement.ValueKind switch
            {
                JsonValueKind.Number => codeElement.GetRawText(),
                JsonValueKind.String => codeElement.GetString(),
                _ => null,
            }
            : null;
        var message = error.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        return (code, message);
    }

    /// <summary>
    /// HTTP 状态码为主（13.1.4）：401 → InvalidKey；403 / 429 → QuotaExceeded；
    /// 400 与 5xx → Engine。状态码未覆盖时（如 200 里带 error）用 error.code 兜底。
    /// </summary>
    internal static TranslationErrorType MapStatus(HttpStatusCode status) => (int)status switch
    {
        401 => TranslationErrorType.InvalidKey,
        403 or 429 => TranslationErrorType.QuotaExceeded,
        400 => TranslationErrorType.Engine,
        >= 500 => TranslationErrorType.Engine,
        _ => TranslationErrorType.Engine,
    };

    /// <summary>
    /// error.code 辅助归类（13.1.4）：仅处理状态码未覆盖的情形，故未列出的返回 null。
    /// 401001 为实测返回值（无凭据）；401000 / 401002 见文档与官方错误码表，一并纳入。
    /// </summary>
    internal static TranslationErrorType? MapErrorCode(string? code) => code switch
    {
        "401000" or "401001" or "401002" => TranslationErrorType.InvalidKey,
        "403001" => TranslationErrorType.QuotaExceeded, // 订阅无权限 / 额度耗尽
        "429001" or "429002" => TranslationErrorType.QuotaExceeded,
        _ => null,
    };

    /// <summary>状态码与错误码共同决定分类：状态码有明确语义时以状态码为准。</summary>
    internal static TranslationErrorType ResolveErrorType(HttpStatusCode? status, string? code)
    {
        if (status is not null)
        {
            var byStatus = MapStatus(status.Value);
            if (byStatus != TranslationErrorType.Engine)
            {
                return byStatus;
            }
        }

        return MapErrorCode(code) ?? TranslationErrorType.Engine;
    }

    /// <summary>构造带分类与用户可读文案的异常（文案不含 Key）。</summary>
    internal static TranslationException CreateError(HttpStatusCode? status, string? code, string? message)
    {
        var errorType = ResolveErrorType(status, code);
        var detail = string.IsNullOrWhiteSpace(message) ? "" : $"（{message}）";

        // 排障用的「状态码 / 错误码」后缀，两个都可能缺
        var parts = new List<string>(2);
        if (status is not null)
        {
            parts.Add($"HTTP {(int)status.Value}");
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            parts.Add($"错误码 {code}");
        }

        var suffix = parts.Count == 0 ? "" : $"（{string.Join(" / ", parts)}）";

        var text = errorType switch
        {
            TranslationErrorType.InvalidKey =>
                $"Azure 密钥无效或未授权{detail}，请检查订阅密钥与区域{suffix}",
            TranslationErrorType.QuotaExceeded =>
                $"Azure 额度用尽或触发限流{detail}，请检查订阅配额{suffix}",
            _ => $"Azure 引擎异常{detail}{suffix}",
        };

        return new TranslationException(errorType, text);
    }
}
