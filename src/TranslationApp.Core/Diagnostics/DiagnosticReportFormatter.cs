using System.Text;
using System.Text.RegularExpressions;

namespace TranslationApp.Core.Diagnostics;

/// <summary>诊断报告文本格式化与脱敏。所有导出路径共用，避免 UI 或日志各写一套泄漏规则。</summary>
public static partial class DiagnosticReportFormatter
{
    private static readonly Regex UrlCredentialPattern = new(
        @"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)\b(api[_-]?key|access[_-]?key|token|password|passwd|pwd|secret|用户名|密码)\s*[:=]\s*([^\s,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Format(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("速译诊断报告");
        builder.Append("生成时间（UTC）：").AppendLine(report.GeneratedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.Append("应用版本：").AppendLine(report.AppVersion);
        builder.Append("便携模式：").AppendLine(report.PortableMode ? "是" : "否");
        builder.Append("汇总：").AppendLine(report.SummaryText);
        builder.AppendLine("说明：报告仅包含运行状态与错误类型，不包含翻译内容、密钥或代理凭据。");
        builder.AppendLine();

        foreach (var result in report.Results)
        {
            builder.Append('[').Append(StatusText(result.Status)).Append("] ")
                .AppendLine(Sanitize(result.Title));
            builder.Append("  ").AppendLine(Sanitize(result.Detail));
            if (result.HasRecommendation)
            {
                builder.Append("  建议：").AppendLine(Sanitize(result.Recommendation!));
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string StatusText(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => "通过",
        DiagnosticStatus.Warning => "警告",
        DiagnosticStatus.Failed => "失败",
        _ => "跳过",
    };

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sanitized = text;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            sanitized = sanitized.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        sanitized = UrlCredentialPattern.Replace(sanitized, "$1***@");
        sanitized = SecretAssignmentPattern.Replace(sanitized, "$1=***");
        return sanitized;
    }
}
