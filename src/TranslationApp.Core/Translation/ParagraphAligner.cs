namespace TranslationApp.Core.Translation;

/// <summary>
/// 段落切分与对齐（FR-043 / spec §1）：长文（≥3 段）时小窗可切「逐段对照」。
/// 对齐成立的条件 = 原文与译文按同一切分规则段数相等且 ≥3；否则返回 null 回整块显示。
/// 纯函数、无 IO。
/// </summary>
public static class ParagraphAligner
{
    /// <summary>低于该段数不值得开对照视图（整块读更顺，spec §1）。</summary>
    public const int MinParagraphs = 3;

    /// <summary>按空行切段；全文无空行时退化为按单换行切。段内容 Trim，空段丢弃。</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var blocks = text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Aggregate(
                new List<List<string>> { new() },
                (lines, raw) =>
                {
                    if (raw.Trim().Length == 0 && lines[^1].Count > 0)
                    {
                        lines.Add(new List<string>());
                    }
                    else if (raw.Trim().Length > 0)
                    {
                        lines[^1].Add(raw.Trim());
                    }
                    return lines;
                })
            .Where(block => block.Count > 0)
            .Select(block => string.Join('\n', block))
            .ToList();

        if (blocks.Count > 1) return blocks;

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    /// <summary>逐段配对；段数不等或不足 <see cref="MinParagraphs"/> 段返回 null。</summary>
    public static IReadOnlyList<(string Source, string Translated)>? Align(string source, string translated)
    {
        var srcParts = Split(source);
        var tgtParts = Split(translated);
        if (srcParts.Count < MinParagraphs || srcParts.Count != tgtParts.Count)
        {
            return null;
        }

        var pairs = new List<(string, string)>(srcParts.Count);
        for (var i = 0; i < srcParts.Count; i++)
        {
            pairs.Add((srcParts[i], tgtParts[i]));
        }

        return pairs;
    }
}
