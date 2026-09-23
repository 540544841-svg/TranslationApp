using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TranslationApp.Core.Translation;

/// <summary>一次术语替换的命中明细（供 UI 显示「术语 ×N」与 tooltip）。</summary>
public sealed record GlossaryReplacement(string Source, string Target, int Count);

/// <summary>替换结果：文本 + 总命中数 + 按词条聚合的明细 + 反向保护跳过的冲突词条（FR-041）。</summary>
public sealed record GlossaryResult(
    string Text,
    int Hits,
    IReadOnlyList<GlossaryReplacement> Applied,
    IReadOnlyList<GlossaryReplacement> Conflicts);

/// <summary>
/// 术语表后置替换（P0 批 1 / spec §1.2，纯函数、永不抛异常）。
/// 规则：长词优先；纯 ASCII 词条做词边界感知的大小写不敏感匹配（"AI" 不伤 "OpenAI"），
/// 含非 ASCII 的词条按子串匹配；命中处先落私有区占位符，全部词条处理完再回填目标词，
/// 保证已替换文本不会被后续词条二次命中。
/// 批 3（FR-041）加反向保护：传入原文时，Target 已出现在原文中的词条**跳过替换**并计入 Conflicts
/// ——Target 撞日常词时后置替换会把不该换的换掉，宁可不换。
/// </summary>
public static class GlossaryReplacer
{
    /// <summary>词条数量上限（Parse / Apply 两端都截断超出部分）。</summary>
    public const int MaxItems = 500;

    private const char TokenOpen = '\uE000';
    private const char TokenClose = '\uE001';

    public static GlossaryResult Apply(
        string translated, IReadOnlyList<GlossaryItem> items, string? sourceText = null,
        string? sourceLanguage = null, string? targetLanguage = null)
    {
        if (string.IsNullOrEmpty(translated) || items is not { Count: > 0 })
            return new GlossaryResult(translated ?? string.Empty, 0,
                Array.Empty<GlossaryReplacement>(), Array.Empty<GlossaryReplacement>());

        try
        {
            var usable = items
                .Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Source) && !string.IsNullOrWhiteSpace(i.Target)
                            && AppliesToLanguage(i, sourceLanguage, targetLanguage))
                .OrderByDescending(i => i.Source.Length)
                .Take(MaxItems)
                .ToList();

            var text = translated;
            var tokens = new List<(string Token, string Target)>();
            var applied = new List<GlossaryReplacement>();
            var conflicts = new List<GlossaryReplacement>();
            var totalHits = 0;

            foreach (var item in usable)
            {
                try
                {
                    // 反向保护（FR-041）：原文里已有 Target → 该词对本条内容不可靠，跳过
                    if (sourceText is not null
                        && sourceText.Contains(item.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(new GlossaryReplacement(item.Source, item.Target, 0));
                        continue;
                    }

                    var token = $"{TokenOpen}{tokens.Count}{TokenClose}";
                    var (replaced, count) = ReplaceOne(text, item, token);
                    if (count == 0) continue;

                    tokens.Add((token, item.Target));
                    text = replaced;
                    totalHits += count;
                    applied.Add(new GlossaryReplacement(item.Source, item.Target, count));
                }
                catch
                {
                    // 单条无效正则只跳过该条，不能让整张术语表失效。
                }
            }

            foreach (var (token, target) in tokens)
                text = text.Replace(token, target, StringComparison.Ordinal);

            return new GlossaryResult(text, totalHits, applied, conflicts);
        }
        catch
        {
            // 术语替换永远不能弄坏翻译结果（spec §1.2-5）
            return new GlossaryResult(translated, 0,
                Array.Empty<GlossaryReplacement>(), Array.Empty<GlossaryReplacement>());
        }
    }

    private static (string, int) ReplaceOne(string text, GlossaryItem item, string token)
    {
        var mode = GlossaryMatchModes.Normalize(item.MatchMode);
        if (mode == GlossaryMatchModes.Regex)
        {
            var regex = new Regex(
                item.Source,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            var count = regex.Matches(text).Count;
            return count > 0 ? (regex.Replace(text, token), count) : (text, 0);
        }

        if (mode is GlossaryMatchModes.Contains or GlossaryMatchModes.CaseSensitive)
        {
            var comparison = mode == GlossaryMatchModes.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var count = CountOccurrences(text, item.Source, comparison);
            return count > 0 ? (ReplaceOrdinal(text, item.Source, token, comparison), count) : (text, 0);
        }

        if (item.Source.All(c => c < 128))
        {
            // ASCII：词首/词尾分别判断是否需要词界（"c++" 结尾非词字符，不要求后置边界）
            var pattern = (char.IsLetterOrDigit(item.Source[0]) ? @"\b" : "")
                + Regex.Escape(item.Source)
                + (char.IsLetterOrDigit(item.Source[^1]) ? @"\b" : "");
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var count = regex.Matches(text).Count;
            return count > 0 ? (regex.Replace(text, token), count) : (text, 0);
        }

        // 含非 ASCII（CJK 等无词界）：OrdinalIgnoreCase 子串扫描
        var sb = new StringBuilder();
        var pos = 0;
        var hits = 0;
        while (true)
        {
            var idx = text.IndexOf(item.Source, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }
            sb.Append(text, pos, idx - pos);
            sb.Append(token);
            pos = idx + item.Source.Length;
            hits++;
        }
        return hits > 0 ? (sb.ToString(), hits) : (text, 0);
    }

    /// <summary>
    private static bool AppliesToLanguage(GlossaryItem item, string? sourceLanguage, string? targetLanguage)
    {
        return LanguageMatches(item.SourceLanguage, sourceLanguage)
               && LanguageMatches(item.TargetLanguage, targetLanguage);
    }

    private static bool LanguageMatches(string scope, string? actual)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "*") return true;
        if (string.IsNullOrWhiteSpace(actual)) return true;
        return string.Equals(scope.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string needle, StringComparison comparison)
    {
        var count = 0;
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(needle, start, comparison);
            if (index < 0) break;
            count++;
            start = index + needle.Length;
        }

        return count;
    }

    private static string ReplaceOrdinal(
        string text, string source, string target, StringComparison comparison)
    {
        var builder = new StringBuilder();
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(source, start, comparison);
            if (index < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            builder.Append(text, start, index - start);
            builder.Append(target);
            start = index + source.Length;
        }

        return builder.ToString();
    }

    /// 解析设置里的术语 JSON。损坏（手改配置 / 版本不兼容）时返回空表并置 corrupted，
    /// 由 UI 提示「按空表运行」；自动过滤空词条并截断到 <see cref="MaxItems"/>。
    /// </summary>
    public static List<GlossaryItem> Parse(string? json, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(json)) return new List<GlossaryItem>();

        try
        {
            var raw = JsonSerializer.Deserialize<List<GlossaryItem>>(json);
            if (raw is null) { corrupted = true; return new List<GlossaryItem>(); }
            return raw
                .Where(i => !string.IsNullOrWhiteSpace(i?.Source) && !string.IsNullOrWhiteSpace(i?.Target))
                .Select(i => i with
                {
                    Source = i.Source.Trim(),
                    Target = i.Target.Trim(),
                    SourceLanguage = string.IsNullOrWhiteSpace(i.SourceLanguage) ? "*" : i.SourceLanguage.Trim(),
                    TargetLanguage = string.IsNullOrWhiteSpace(i.TargetLanguage) ? "*" : i.TargetLanguage.Trim(),
                    MatchMode = GlossaryMatchModes.Normalize(i.MatchMode),
                    Note = i.Note?.Trim() ?? "",
                })
                .Take(MaxItems)
                .ToList();
        }
        catch (JsonException)
        {
            corrupted = true;
            return new List<GlossaryItem>();
        }
    }

    public static string Serialize(IReadOnlyList<GlossaryItem> items) => JsonSerializer.Serialize(items);
}
