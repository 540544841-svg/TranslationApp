using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// Google 非官方接口响应解析器（FR-006）。
/// 响应为嵌套数组：[[["译文段1","原文段1",...],["译文段2",...],...],...,"检测语言",...]
/// 译文 = 所有段落第 0 列拼接；第 3 个元素（root[2]）为检测到的源语言。
/// </summary>
public static class GoogleResponseParser
{
    public static (string TranslatedText, string? DetectedSourceLanguage) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, "响应不是有效的 JSON", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, "响应格式异常：根节点不是数组");
            }

            if (root[0].ValueKind != JsonValueKind.Array)
            {
                throw new TranslationException(TranslationErrorType.Engine, "响应格式异常：缺少译文段落数组");
            }

            var translated = new StringBuilder();
            foreach (var segment in root[0].EnumerateArray())
            {
                // 每段形如 ["译文","原文",null,null,10]；译文段第 0 列为字符串才拼接
                if (segment.ValueKind == JsonValueKind.Array
                    && segment.GetArrayLength() > 0
                    && segment[0].ValueKind == JsonValueKind.String)
                {
                    translated.Append(segment[0].GetString());
                }
            }

            string? detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
                ? root[2].GetString()
                : null;

            return (translated.ToString(), detected);
        }
    }
}
