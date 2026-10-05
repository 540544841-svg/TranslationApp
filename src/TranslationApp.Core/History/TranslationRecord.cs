namespace TranslationApp.Core.History;

/// <summary>一条翻译记录（FR-014）。</summary>
public sealed record TranslationRecord(
    long Id,
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string Engine,
    bool Reviewed = false,
    bool Rejected = false,
    DateTimeOffset? EditedAt = null,
    bool Pinned = false)
{
    /// <summary>列表展示用时间（本地时区，精确到分钟）。</summary>
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>
    /// 印谱行右端的时间（设计稿 `.log .li .when`）：一小时内给相对量，当天给时刻，再往前给日期。
    /// 取的是绑定那一刻的快照，列表每次重新载入都会重算。
    /// </summary>
    public string CreatedAtRelative
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            var now = DateTimeOffset.Now;
            var delta = now - local;
            if (delta < TimeSpan.FromMinutes(1))
            {
                return "刚刚";   // 含时间戳在未来（时区/时钟漂移）的情况
            }

            if (delta < TimeSpan.FromHours(1))
            {
                return $"{(int)delta.TotalMinutes} 分钟前";
            }

            if (local.Date == now.Date)
            {
                return $"今天 {local:HH:mm}";
            }

            if (local.Date == now.Date.AddDays(-1))
            {
                return $"昨天 {local:HH:mm}";
            }

            return local.ToString("MM-dd HH:mm");
        }
    }

    /// <summary>TM 质量徽标文案；空串表示普通记录。</summary>
    public string QualityBadge => Rejected ? "禁用复用" : Reviewed ? "已校对" : "";

    public bool HasQualityBadge => QualityBadge.Length > 0;

    /// <summary>搜索/列表用的一行摘要（原文单行化并截断）。</summary>
    public string SourceSummary => Summarize(SourceText);

    /// <summary>译文摘要。</summary>
    public string TranslatedSummary => Summarize(TranslatedText);

    /// <summary>
    /// 列表里的短引擎名：印谱一行只放得下一枚胶囩，去掉「（非官方，零配置）」这类补充说明。
    /// 库里的 Engine 列存的仍是目录全名（筛选靠它精确匹配），这里只负责显示。
    /// </summary>
    public string EngineShort => ShortEngineName(Engine);

    public static string ShortEngineName(string name)
    {
        var index = name.IndexOf('（');
        return index > 0 ? name[..index] : name;
    }

    private static string Summarize(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 60 ? single : single[..60] + "…";
    }
}

/// <summary>生词本条目（FR-015）。独立于历史记录，不受历史清理影响。</summary>
public sealed record VocabularyEntry(
    long Id,
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage)
{
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>藏印行右端的相对时间（设计稿 `.word-item .when`：3 天前 / 上周）。</summary>
    public string CreatedAtRelative
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            var days = (DateTimeOffset.Now.Date - local.Date).Days;
            return days switch
            {
                <= 0 => "今天",
                1 => "昨天",
                < 7 => $"{days} 天前",
                < 14 => "上周",
                _ => local.ToString("MM-dd"),
            };
        }
    }

    public string LanguagePairDisplay => $"{SourceLanguage} → {TargetLanguage}";

    /// <summary>语言对的紧凑写法（设计稿 .word-item 的胶囩：EN → 中）：一行只放得下两个字宽。</summary>
    public string LanguagePairTag => $"{Compact(SourceLanguage)} → {Compact(TargetLanguage)}";

    private static string Compact(string code)
    {
        var primary = code.Split('-', '_')[0].ToLowerInvariant();
        return primary switch
        {
            "zh" => "中",
            "ja" => "日",
            "ko" => "韩",
            "auto" => "自动",
            _ => primary.ToUpperInvariant(),
        };
    }
}
