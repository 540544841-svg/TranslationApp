using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// Bing ttranslatev3 响应解析器。
/// 正常响应：[{"translations":[{"text":"你好","to":"zh-Hans"}],"detectedLanguage":{"language":"en"}}]
/// 异常响应：{"statusCode":400,"errorMessage":"..."}（令牌失效等）
/// </summary>
public static class BingResponseParser
{
    /// <summary>解析译文与检测到的源语言（无检测信息时返回 null）。</summary>
    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, "引擎返回空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "引擎响应格式异常", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("errorMessage", out var error))
            {
                throw new TranslationException(
                    TranslationErrorType.Engine, $"引擎返回错误：{error.GetString()}");
            }

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "引擎响应结构异常");
            }

            var first = root[0];
            if (!first.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "引擎响应缺少译文");
            }

            var text = translations[0].TryGetProperty("text", out var textElement)
                ? textElement.GetString() ?? ""
                : "";

            string? detected = null;
            if (first.TryGetProperty("detectedLanguage", out var detectedElement)
                && detectedElement.ValueKind == JsonValueKind.Object
                && detectedElement.TryGetProperty("language", out var languageElement))
            {
                detected = languageElement.GetString();
            }

            if (text.Length == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "引擎返回空译文");
            }

            return (text, detected);
        }
    }
}
