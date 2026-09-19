using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 百度翻译（标准版通用文本翻译）响应解析器（13.1.3）。
/// 成功：{"from":"en","to":"zh","trans_result":[{"src":"...","dst":"..."}]}
/// 失败：{"error_code":"54001","error_msg":"Invalid Sign"}（error_code 可能是字符串或数字，两种都要兼容）
/// </summary>
public static class BaiduResponseParser
{
    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, "百度翻译返回空响应");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "百度翻译响应格式异常", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TranslationException(TranslationErrorType.Engine, "百度翻译响应结构异常");
            }

            if (root.TryGetProperty("error_code", out var errorCodeElement))
            {
                var code = ReadErrorCode(errorCodeElement);
                var message = root.TryGetProperty("error_msg", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                throw CreateError(code, message);
            }

            if (!root.TryGetProperty("trans_result", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "百度翻译响应缺少译文");
            }

            // 百度按换行拆分为多条结果，用 \n 重新拼接才能还原原文的分段结构
            var builder = new StringBuilder();
            foreach (var item in results.EnumerateArray())
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(item.TryGetProperty("dst", out var dst) ? dst.GetString() : null);
            }

            var text = builder.ToString();
            if (text.Length == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "百度翻译返回空译文");
            }

            var detected = root.TryGetProperty("from", out var fromElement) && fromElement.ValueKind == JsonValueKind.String
                ? fromElement.GetString()
                : null;

            return (text, detected);
        }
    }

    /// <summary>error_code 在不同接口版本下可能是字符串或数字（13.1.3），统一取十进制文本。</summary>
    internal static string ReadErrorCode(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        _ => "",
    };

    /// <summary>
    /// 错误码归类（13.1.3）。52001 归入 Network，使基类按网络类错误重试 1 次；
    /// 未列出的错误码一律归入 Engine（不新增枚举值，13.1.1）。
    /// </summary>
    internal static TranslationErrorType MapErrorCode(string code) => code switch
    {
        "52003" or "54001" or "90107" => TranslationErrorType.InvalidKey,
        "54003" or "54004" or "54005" => TranslationErrorType.QuotaExceeded,
        "52001" => TranslationErrorType.Network,
        _ => TranslationErrorType.Engine,
    };

    private static TranslationException CreateError(string? code, string? engineMessage)
    {
        var errorType = MapErrorCode(code ?? "");
        var detail = string.IsNullOrWhiteSpace(engineMessage) ? "" : $"（{engineMessage}）";
        var message = errorType switch
        {
            TranslationErrorType.InvalidKey =>
                $"百度翻译密钥无效或未授权{detail}，请检查 APPID 与密钥（错误码 {code}）",
            TranslationErrorType.QuotaExceeded =>
                $"百度翻译额度用尽或触发限流{detail}",
            TranslationErrorType.Network =>
                $"百度翻译请求超时{detail}",
            _ => $"百度翻译引擎异常{detail}（错误码 {code}）",
        };

        return new TranslationException(errorType, message);
    }
}
