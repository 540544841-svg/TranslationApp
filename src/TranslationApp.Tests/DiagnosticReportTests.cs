using System.Globalization;
using TranslationApp.Core.Diagnostics;
using Xunit;

namespace TranslationApp.Tests;

public sealed class DiagnosticReportTests
{
    [Fact]
    public void Format_CountsStatusesAndIncludesRecommendation()
    {
        var report = new DiagnosticReport(
            DateTimeOffset.Parse("2026-09-23T12:34:56Z", CultureInfo.InvariantCulture),
            "0.1.0",
            true,
            [
                new DiagnosticResult("passed", "通过项", DiagnosticStatus.Passed, "正常", null),
                new DiagnosticResult("warning", "警告项", DiagnosticStatus.Warning, "需注意", "请检查配置"),
                new DiagnosticResult("failed", "失败项", DiagnosticStatus.Failed, "错误"),
                new DiagnosticResult("skipped", "跳过项", DiagnosticStatus.Skipped, "未启用"),
            ]);

        var text = DiagnosticReportFormatter.Format(report);

        Assert.Contains("4 项检查：1 项通过，1 项警告，1 项失败，1 项跳过", text, StringComparison.Ordinal);
        Assert.Contains("建议：请检查配置", text, StringComparison.Ordinal);
        Assert.Contains("[失败] 失败项", text, StringComparison.Ordinal);
        Assert.Contains("不包含翻译内容、密钥或代理凭据", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RedactsProfilePathAndCredentialAssignments()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var input = $"{profile}\\data\\settings.json token=abc password=xyz  https://user:pass@example.test/path";

        var text = DiagnosticReportFormatter.Sanitize(input);

        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
        Assert.Contains("token=***", text, StringComparison.Ordinal);
        Assert.Contains("password=***", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_ExcludesContentBearingFieldsByConstruction()
    {
        var report = new DiagnosticReport(
            DateTimeOffset.UtcNow,
            "0.1.0",
            false,
            [
                new DiagnosticResult("database", "数据库", DiagnosticStatus.Passed, "quick_check=ok"),
            ]);

        var text = DiagnosticReportFormatter.Format(report);

        Assert.DoesNotContain("SourceText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslatedText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", text, StringComparison.Ordinal);
    }
}
