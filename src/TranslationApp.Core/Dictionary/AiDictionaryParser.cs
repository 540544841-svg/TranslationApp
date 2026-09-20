using System.Text;
using System.Text.Json;

namespace TranslationApp.Core.Dictionary;

/// <summary>
/// AI 词典条目（FR-056 / 批 6 spec §1）：模型按约定返回的结构化释义。
/// <see cref="Definition"/> 是给小窗词典卡看的一段纯文本（音标 + 逐条释义）。
/// </summary>
public sealed record AiDictionaryEntry(string Wordhead, string? Phonetic, IReadOnlyList<string> Senses)
{
    public string Definition
    {
        get
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Phonetic))
            {
                builder.Append(Phonetic.Trim()).Append('\n');
            }

            builder.Append(string.Join("\n", Senses));
            return builder.ToString().Trim();
        }
    }
}

/// <summary>
/// 把 AI 模型返回的文本解析成 <see cref="AiDictionaryEntry"/>（FR-056）。
/// 模型经常不老实：包一层 ```json 围栏、前后加一句"好的，以下是结果："、用单引号或留尾逗号——
/// 所以这里做的是**容错提取**而不是严格反序列化：拿不到可用结构就返回 null，
/// 让小窗干脆不显示词典卡，也绝不把半截 JSON 甩给用户看。
/// </summary>
public static class AiDictionaryParser
{
    /// <summary>释义条数上限（多义词给十几条会把小窗撑爆）。</summary>
    public const int MaxSenses = 6;

    /// <summary>单条释义长度上限（防模型写小作文）。</summary>
    public const int MaxSenseChars = 160;

    private static readonly string[] WordheadKeys = ["wordhead", "word", "term", "词头"];
    private static readonly string[] PhoneticKeys = ["phonetic", "phonetics", "ipa", "音标"];
    private static readonly string[] SenseKeys = ["senses", "sense", "meanings", "definitions", "释义", "简明释义"];

    public static AiDictionaryEntry? Parse(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(Repair(json));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var wordhead = ReadString(root, WordheadKeys);
        var phonetic = ReadString(root, PhoneticKeys);
        var senses = ReadSenses(root);
        // 没有释义的条目对词典卡毫无价值（只有词头的话不如不显示），一律按解析失败处理
        if (senses.Count == 0)
        {
            return null;
        }

        return new AiDictionaryEntry(wordhead ?? "", phonetic, senses);
    }

    /// <summary>取第一个 '{' 到最后一个 '}' 之间的片段（剥掉代码围栏与前后散文）。</summary>
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open)
        {
            return null;
        }

        return text[open..(close + 1)];
    }

    /// <summary>
    /// 只修一件事：删掉对象/数组收尾前的多余逗号。
    /// 自己扫字符串而不是用正则——正则会把 <c>"a, }"</c> 这种正文里的逗号也吃掉。
    /// </summary>
    private static string Repair(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                builder.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                continue;
            }

            if (c != ',')
            {
                builder.Append(c);
                continue;
            }

            // 逗号：后面（跳过空白）是收尾括号就是尾逗号，丢掉
            var next = i + 1;
            while (next < json.Length && char.IsWhiteSpace(json[next]))
            {
                next++;
            }

            if (next < json.Length && (json[next] == '}' || json[next] == ']'))
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string? ReadString(JsonElement root, string[] keys)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!Matches(keys, property.Name))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Array
                     && property.Value.GetArrayLength() > 0
                     && property.Value[0].ValueKind == JsonValueKind.String)
            {
                var first = property.Value[0].GetString();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    return first.Trim();
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadSenses(JsonElement root)
    {
        var result = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!Matches(SenseKeys, property.Name))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    Add(result, property.Value.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            Add(result, item.GetString());
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            // 有的模型给 [{"type":"v.","meaning":"放弃"}]：把值串成一条
                            Add(result, string.Join(' ', item.EnumerateObject().Select(p => p.Value.ToString()).Where(v => !string.IsNullOrWhiteSpace(v))));
                        }

                        if (result.Count >= MaxSenses)
                        {
                            return result;
                        }
                    }

                    break;
            }
        }

        return result;

        void Add(List<string> list, string? value)
        {
            var text = value?.Trim() ?? "";
            if (text.Length == 0)
            {
                return;
            }

            list.Add(text.Length <= MaxSenseChars ? text : text[..MaxSenseChars] + "…");
        }
    }

    private static bool Matches(string[] keys, string name) =>
        keys.Any(k => string.Equals(k, name.Replace("_", "").Trim(), StringComparison.OrdinalIgnoreCase));
}
