namespace TranslationApp.Core.History;

/// <summary>一条翻译记录（FR-014）。</summary>
public sealed record TranslationRecord(
    long Id,
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslatedText,
    string SourceLanguage,
    string TargetLanguage,
    string Engine)
{
    /// <summary>列表展示用时间（本地时区，精确到分钟）。</summary>
    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>搜索/列表用的一行摘要（原文单行化并截断）。</summary>
    public string SourceSummary => Summarize(SourceText);

    /// <summary>译文摘要。</summary>
    public string TranslatedSummary => Summarize(TranslatedText);

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

    public string LanguagePairDisplay => $"{SourceLanguage} → {TargetLanguage}";
}
