namespace TranslationApp.Core.History;

/// <summary>一个会话组（FR-057）：连续翻译的一批记录，标签给时间范围、条数与首条摘要。</summary>
public sealed record HistoryGroup(
    string Label,
    DateTimeOffset Start,
    DateTimeOffset End,
    int Count,
    IReadOnlyList<TranslationRecord> Records);

/// <summary>
/// 历史按会话分组（FR-057 / 批 6 spec §2）：卖家找"昨天调那个 listing 的十来个词"时，
/// 逐条翻不如按段翻——同一次连续翻译（相邻间隔 ≤30 分钟）归一组。
/// 纯函数、只整理视图层数据：仓储、导出、删除都仍按单条 Id 走，分组不落库、不改 schema。
/// </summary>
public static class HistoryGrouper
{
    /// <summary>相邻两条超过这个间隔就断组（分钟）。</summary>
    public const int WindowMinutes = 30;

    /// <summary>组标签里首条原文的摘要长度。</summary>
    private const int SummaryChars = 24;

    /// <summary>
    /// 按时间**倒序**分组（新组在前，与历史页现有排序一致）；输入乱序会先按时间排。
    /// 空输入返回空表。
    /// </summary>
    public static IReadOnlyList<HistoryGroup> Group(
        IReadOnlyList<TranslationRecord> records, int windowMinutes = WindowMinutes)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var ordered = records.OrderByDescending(r => r.CreatedAt).ToArray();
        var span = TimeSpan.FromMinutes(Math.Max(1, windowMinutes));
        var groups = new List<HistoryGroup>();
        var bucket = new List<TranslationRecord> { ordered[0] };

        for (var i = 1; i < ordered.Length; i++)
        {
            // 倒序遍历：上一条比这一条新，差值同样按绝对值算
            if ((bucket[^1].CreatedAt - ordered[i].CreatedAt) > span)
            {
                groups.Add(Build(bucket));
                bucket = [];
            }

            bucket.Add(ordered[i]);
        }

        groups.Add(Build(bucket));
        return groups;
    }

    private static HistoryGroup Build(List<TranslationRecord> bucket)
    {
        var end = bucket[0].CreatedAt;
        var start = bucket[^1].CreatedAt;
        var summary = SingleLine(bucket[^1].SourceText);
        if (summary.Length > SummaryChars)
        {
            summary = summary[..SummaryChars] + "…";
        }

        var label = $"{start.ToLocalTime():MM-dd HH:mm} – {end.ToLocalTime():HH:mm} · {bucket.Count} 条"
                    + (summary.Length > 0 ? $" · {summary}" : "");
        return new HistoryGroup(label, start, end, bucket.Count, bucket);
    }

    private static string SingleLine(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
