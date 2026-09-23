using System.Text;

namespace TranslationApp.Core.Updates;

/// <summary>一次已经下载、验签、验哈希的更新安装计划。</summary>
public sealed record UpdateApplyPlan(string CandidatePath, string TargetPath, string BackupPath, string ScriptPath);

/// <summary>
/// Windows 单文件安装器脚本生成。核心约束：
/// 1) 候选 EXE 先复制到目标同目录，最终替换是同卷 Move，不跨卷复制；
/// 2) 先保存旧 EXE，替换或启动任一步失败都恢复旧 EXE；
/// 3) 脚本最后删除自身，失败时保留日志供用户排查。
/// </summary>
public static class UpdateInstaller
{
    public static UpdateApplyPlan Prepare(string downloadedExePath, string targetExePath, string? stagingDirectory = null)
    {
        var downloaded = Path.GetFullPath(downloadedExePath);
        var target = Path.GetFullPath(targetExePath);
        if (!File.Exists(downloaded))
        {
            throw new FileNotFoundException("更新包不存在", downloaded);
        }

        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("无法确定程序安装目录");
        Directory.CreateDirectory(targetDirectory);

        var directory = stagingDirectory is null
            ? targetDirectory
            : Path.GetFullPath(stagingDirectory);
        Directory.CreateDirectory(directory);

        var fileName = $"{Path.GetFileNameWithoutExtension(target)}.update-{Guid.NewGuid():N}.exe";
        var candidate = Path.Combine(directory, fileName);
        File.Copy(downloaded, candidate, overwrite: true);

        var backup = target + ".rollback";
        var script = Path.Combine(Path.GetTempPath(), $"TranslationApp-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, BuildScript(candidate, target, backup), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return new UpdateApplyPlan(candidate, target, backup, script);
    }

    internal static string BuildScript(string candidatePath, string targetPath, string backupPath)
    {
        var pid = Environment.ProcessId;
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$pidToWait = {pid}");
        script.AppendLine($"$target = {PowerShellLiteral(targetPath)}");
        script.AppendLine($"$candidate = {PowerShellLiteral(candidatePath)}");
        script.AppendLine($"$backup = {PowerShellLiteral(backupPath)}");
        script.AppendLine("$log = Join-Path $env:TEMP 'TranslationApp-update.log'");
        script.AppendLine("try {");
        script.AppendLine("    while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }");
        script.AppendLine("    if (Test-Path -LiteralPath $target) {");
        script.AppendLine("        Copy-Item -LiteralPath $target -Destination $backup -Force");
        script.AppendLine("    }");
        script.AppendLine("    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue");
        script.AppendLine("    Move-Item -LiteralPath $candidate -Destination $target -Force");
        script.AppendLine("    Start-Process -FilePath $target -ArgumentList '--updated'");
        script.AppendLine("    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue");
        script.AppendLine($"    Add-Content -LiteralPath $log -Value ('{DateTimeOffset.Now:O} 更新成功: ' + $target)");
        script.AppendLine("    exit 0");
        script.AppendLine("} catch {");
        script.AppendLine($"    Add-Content -LiteralPath $log -Value ('{DateTimeOffset.Now:O} 更新失败: ' + $_.Exception.Message)");
        script.AppendLine("    if (Test-Path -LiteralPath $backup) {");
        script.AppendLine("        Copy-Item -LiteralPath $backup -Destination $target -Force");
        script.AppendLine("        Start-Process -FilePath $target -ArgumentList '--update-rollback'");
        script.AppendLine("    }");
        script.AppendLine("    exit 1");
        script.AppendLine("} finally {");
        script.AppendLine("    Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue");
        script.AppendLine("    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        script.AppendLine("}");
        return script.ToString();
    }

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
