namespace TranslationApp.Core.History;

/// <summary>历史列表筛选条件（关键字 + 常用元数据）。</summary>
public sealed record HistoryQuery
{
    public string Keyword { get; init; } = "";
    public int Limit { get; init; } = 500;

    /// <summary>只看固定记录。</summary>
    public bool PinnedOnly { get; init; }

    /// <summary>空 = 不过滤引擎。</summary>
    public string? Engine { get; init; }

    /// <summary>空 = 不过滤目标语言。</summary>
    public string? TargetLanguage { get; init; }

    /// <summary>空 = 不过滤时间。</summary>
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }
}

/// <summary>时间范围下拉项（历史页使用，实际时间边界由 UI 计算）。</summary>
public static class HistoryTimeRanges
{
    public const string All = "all";
    public const string Today = "today";
    public const string Week = "week";
    public const string Month = "month";

    public static DateTimeOffset? StartOf(string value) => value switch
    {
        Today => DateTimeOffset.Now.Date,
        Week => DateTimeOffset.Now.AddDays(-6).Date,
        Month => DateTimeOffset.Now.AddMonths(-1),
        _ => null,
    };
}
