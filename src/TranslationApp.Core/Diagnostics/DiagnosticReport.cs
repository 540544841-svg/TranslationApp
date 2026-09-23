namespace TranslationApp.Core.Diagnostics;

/// <summary>单项诊断结论。</summary>
public enum DiagnosticStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,
}

/// <summary>一项可导出、可单独阅读的诊断结果。</summary>
public sealed record DiagnosticResult(
    string Id,
    string Title,
    DiagnosticStatus Status,
    string Detail,
    string? Recommendation = null)
{
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);
}

/// <summary>一次完整诊断的快照。报告只携带运行状态，不携带密钥、原文或译文。</summary>
public sealed record DiagnosticReport(
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    bool PortableMode,
    IReadOnlyList<DiagnosticResult> Results)
{
    public int PassedCount => Count(DiagnosticStatus.Passed);

    public int WarningCount => Count(DiagnosticStatus.Warning);

    public int FailedCount => Count(DiagnosticStatus.Failed);

    public int SkippedCount => Count(DiagnosticStatus.Skipped);

    public string SummaryText =>
        $"{Results.Count} 项检查：{PassedCount} 项通过，{WarningCount} 项警告，{FailedCount} 项失败，{SkippedCount} 项跳过";

    private int Count(DiagnosticStatus status) => Results.Count(result => result.Status == status);
}
