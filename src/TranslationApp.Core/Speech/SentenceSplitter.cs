namespace TranslationApp.Core.Speech;

/// <summary>
/// 句子切分（FR-053 影子跟读 / 批 5 spec §5）：跟读要「一句一念」，所以切分必须比朗读更保守——
/// 句末标点、换行都算断点，引号/括号等收尾符号跟着前句走，两三个字的碎句并进前一句
/// （否则朗读会念出「嗯。」这种没有信息量的碎片），超长句按空白强切（SAPI 一次念太久用户跟不上）。
/// 纯函数、无 IO、可单测。
/// </summary>
public static class SentenceSplitter
{
    /// <summary>单句最长字符数，超出按空白/逗号强切。</summary>
    public const int MaxSentenceChars = 200;

    /// <summary>短于此长度（且不是唯一一句）的碎句并进上一句。</summary>
    public const int MinSentenceChars = 3;

    /// <summary>句末标点（中英文都收）。</summary>
    private const string Terminators = "。！？!?…；;：:";

    /// <summary>跟在句末标点后面、属于同一句的收尾符号与空白（\r 归入：CRLF 不该把换行留在句首）。</summary>
    private const string Trailing = "”』」）)】》\"' \t\r";

    /// <summary>把文本切成句子；空白文本返回空表。</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var raw = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r')
            {
                continue; // 断行交给 \n，\r\n 不会切出空句
            }

            if (!IsBreakAt(text, i))
            {
                continue;
            }

            // 换行本身不进正文；句末标点要连同紧随其后的引号/括号一起归入本句
            var end = c == '\n' ? i : AdvancePastTrailing(text, i);
            raw.Add(text[start..end]);
            start = Math.Max(end, i + 1);
            i = start - 1; // 已消费到 start-1，下一轮从 start 继续
        }

        if (start < text.Length)
        {
            raw.Add(text[start..]);
        }

        return MergeAndClamp(raw);
    }

    /// <summary>
    /// 断点判定。半角句点单独处理：小数（3.5）不能被切成两句，故只在「数字夹着句点」时不切。
    /// 代价是 "e.g." / URL 里的点会多切一刀——碎句会被并回去，朗读不会念出半截词，
    /// 因此不为它加缩写特例表（跟读的是译文正文，这类文本极少）。
    /// </summary>
    private static bool IsBreakAt(string text, int i)
    {
        var c = text[i];
        if (c == '\n')
        {
            return true;
        }

        if (c != '.')
        {
            return Terminators.Contains(c);
        }

        return !(i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]));
    }

    /// <summary>
    /// 正文长度（去掉句末标点与收尾符号）：判「碎句」看的是有没有实际内容，
    /// 「好的。」三个字符其实只有两个字，该并进上一句而不是单独念一遍。
    /// </summary>
    private static int ContentLength(string sentence)
    {
        var end = sentence.Length;
        while (end > 0 && (Terminators.Contains(sentence[end - 1]) || Trailing.Contains(sentence[end - 1])))
        {
            end--;
        }

        return end;
    }

    /// <summary>返回句末标点之后、连续收尾符号（含空白）结束处的下标。</summary>
    private static int AdvancePastTrailing(string text, int terminatorIndex)
    {
        var i = terminatorIndex + 1;
        while (i < text.Length && Trailing.Contains(text[i]) && text[i] is not ('\n' or '\r'))
        {
            i++;
        }

        return i;
    }

    /// <summary>丢空、并碎句、强切超长句。</summary>
    private static IReadOnlyList<string> MergeAndClamp(List<string> pieces)
    {
        var merged = new List<string>();
        foreach (var piece in pieces)
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && ContentLength(trimmed) < MinSentenceChars)
            {
                merged[^1] += trimmed;
                continue;
            }

            merged.Add(trimmed);
        }

        var result = new List<string>();
        foreach (var sentence in merged)
        {
            if (sentence.Length <= MaxSentenceChars)
            {
                result.Add(sentence);
                continue;
            }

            result.AddRange(ClampLong(sentence));
        }

        return result;
    }

    /// <summary>超长句强切：每次在限额内找最后一个空白/逗号断开，找不到就硬切（保证一定有进展）。</summary>
    private static IEnumerable<string> ClampLong(string sentence)
    {
        var remaining = sentence;
        while (remaining.Length > MaxSentenceChars)
        {
            var cut = remaining.LastIndexOfAny([' ', '\t', '，', ','], MaxSentenceChars - 1);
            if (cut <= 0)
            {
                cut = MaxSentenceChars;
            }

            yield return remaining[..cut].Trim();
            remaining = remaining[cut..].Trim();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}
