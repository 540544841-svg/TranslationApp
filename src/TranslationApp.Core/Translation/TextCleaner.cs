using System.Text;
using System.Text.RegularExpressions;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 阅读清洗（P0 批 1 / spec §3.1，纯函数）：把 PDF / 论文复制文本里的硬换行还原成连续句子，
/// 仅用于划词与剪贴板自动翻译路径；手动输入不清洗。
/// 直通条件：空、少于 <see cref="MinLength"/> 字符、或单行——划词单词场景零开销。
/// </summary>
public static class TextCleaner
{
    /// <summary>低于该长度的文本不清洗（短多行选择多半是用户有意保留的排版）。</summary>
    public const int MinLength = 80;

    private const string SentenceEnders = "。！？!?；;：:";

    private static readonly Regex ListItemStart = new(
        @"^\s*(?:[-•·*]|\d+[.)）]|[（(]\d+[）)]|[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮])\s*",
        RegexOptions.Compiled);

    public static string CleanForReading(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < MinLength)
            return text ?? string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.Contains('\n'))
            return normalized;

        try
        {
            var lines = normalized.Split('\n');
            lines = RemoveRepeatedHeaderFooter(lines);

            var result = new StringBuilder();
            var paragraph = new List<string>();

            void FlushParagraph()
            {
                if (paragraph.Count == 0) return;
                if (result.Length > 0) result.Append("\n\n");
                result.Append(JoinParagraph(paragraph));
                paragraph.Clear();
            }

            foreach (var line in lines)
            {
                if (line.Trim().Length == 0) FlushParagraph();
                else paragraph.Add(line.TrimEnd());
            }
            FlushParagraph();

            return result.ToString();
        }
        catch
        {
            return normalized; // 清洗失败绝不能弄坏原文
        }
    }

    /// <summary>与首行完全相同、出现 ≥3 次的短行（≤40 字符）视为页眉/页脚删除（spec §3.1-4）。</summary>
    private static string[] RemoveRepeatedHeaderFooter(string[] lines)
    {
        var first = lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (string.IsNullOrEmpty(first) || first.Length > 40) return lines;

        var count = lines.Count(l => l.Trim() == first);
        if (count < 3) return lines;

        return lines.Where(l => l.Trim() != first).ToArray();
    }

    private static string JoinParagraph(List<string> lines)
    {
        var sb = new StringBuilder(lines[0]);
        for (var i = 1; i < lines.Count; i++)
        {
            var prev = sb.ToString();
            var next = lines[i];

            if (ShouldJoin(prev, next))
            {
                if (prev[^1] == '-')
                {
                    sb.Length--; // 连字符断词：去连字符直连
                    sb.Append(next);
                }
                else
                {
                    sb.Append(' ').Append(next);
                }
            }
            else
            {
                sb.Append('\n').Append(next);
            }
        }
        return sb.ToString();
    }

    private static bool ShouldJoin(string prev, string next)
    {
        if (prev.Length == 0 || next.Length == 0) return false;
        if (ListItemStart.IsMatch(next)) return false;   // 列表行不并入上一行
        if (ListItemStart.IsMatch(prev)) return false;   // 列表行后面也不并

        var last = prev[^1];
        var first = next[0];

        // 句末标点后是新句，不合并
        if (SentenceEnders.Contains(last)) return false;

        // 合并触发：行尾是 ASCII 字母数字 / 逗号 / 连字符
        var prevTail = (last < 128 && char.IsLetterOrDigit(last)) || last is ',' or '，' or '-';
        // 被并入行须以 ASCII 字母数字开头（CJK 行首不并，避免中文换行被塞空格）
        var nextHead = first < 128 && char.IsLetterOrDigit(first);

        return prevTail && nextHead;
    }
}
