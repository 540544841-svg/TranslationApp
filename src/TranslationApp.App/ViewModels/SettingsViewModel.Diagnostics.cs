using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using TranslationApp.Core.Diagnostics;
using TranslationApp.Core.Settings;

namespace TranslationApp.ViewModels;

/// <summary>Doctor 诊断中心的运行、导出与结果展示。</summary>
public partial class SettingsViewModel
{
    /// <summary>Doctor 请求切换设置分区；MainWindow 订阅后按页签名导航。</summary>
    public event EventHandler<string>? SettingsNavigationRequested;

    private DiagnosticReport? _doctorReport;

    public ObservableCollection<DiagnosticItemViewModel> DoctorResults { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoctorHasNotRun))]
    private bool _doctorHasRun;

    [ObservableProperty]
    private bool _doctorBusy;

    [ObservableProperty]
    private string _doctorMessage = "点击“开始诊断”执行本地检查；代理与更新检查会联网。";

    [ObservableProperty]
    private string _doctorSummary = "尚未运行诊断";

    public bool DoctorHasNotRun => !DoctorHasRun;

    private bool CanRunDoctor() => !DoctorBusy;

    private bool CanUseDoctorReport() => _doctorReport is not null;

    partial void OnDoctorBusyChanged(bool value) => RunDoctorCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRunDoctor))]
    private async Task RunDoctorAsync()
    {
        if (DoctorBusy)
        {
            return;
        }

        DoctorBusy = true;
        DoctorHasRun = false;
        DoctorResults.Clear();
        _doctorReport = null;
        DoctorSummary = "诊断进行中…";
        DoctorMessage = "正在检查热键、OCR、代理、数据库与更新通道…";
        OnPropertyChanged(nameof(DoctorReportText));
        CopyDoctorReportCommand.NotifyCanExecuteChanged();
        ExportDoctorReportCommand.NotifyCanExecuteChanged();

        try
        {
            var report = await _doctor.RunAsync();
            _doctorReport = report;
            foreach (var result in report.Results)
            {
                DoctorResults.Add(new DiagnosticItemViewModel(result));
            }

            DoctorHasRun = true;
            DoctorSummary = report.SummaryText;
            DoctorMessage = $"诊断完成于 {report.GeneratedAtUtc.ToLocalTime():HH:mm:ss}；报告已按脱敏规则生成。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Doctor 诊断失败");
            DoctorSummary = "诊断未能完成";
            DoctorMessage = $"诊断失败：{DiagnosticReportFormatter.Sanitize(ex.Message)}";
        }
        finally
        {
            DoctorBusy = false;
            OnPropertyChanged(nameof(DoctorReportText));
            CopyDoctorReportCommand.NotifyCanExecuteChanged();
            ExportDoctorReportCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDoctorReport))]
    private void CopyDoctorReport()
    {
        if (_doctorReport is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(DiagnosticReportFormatter.Format(_doctorReport));
            DoctorMessage = "脱敏诊断报告已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制诊断报告失败");
            DoctorMessage = "复制失败，请改用“导出诊断报告”。";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDoctorReport))]
    private void ExportDoctorReport()
    {
        if (_doctorReport is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var dialog = new SaveFileDialog
            {
                Title = "导出速译诊断报告",
                Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = $"速译诊断-{DateTime.Now:yyyyMMdd-HHmm}.txt",
                InitialDirectory = AppPaths.DataDirectory,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            File.WriteAllText(
                dialog.FileName,
                DiagnosticReportFormatter.Format(_doctorReport),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            DoctorMessage = $"已导出 {Path.GetFileName(dialog.FileName)}；报告不含密钥与翻译内容。";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出诊断报告失败");
            DoctorMessage = "导出失败，请检查目标路径是否可写。";
        }
    }

    /// <summary>执行诊断项对应的下一步操作；未知动作只做幂等复测。</summary>
    [RelayCommand]
    private void ExecuteDoctorAction(string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
        {
            return;
        }

        if (actionKey.Equals("rerun", StringComparison.Ordinal))
        {
            _ = RunDoctorAsync();
            return;
        }

        if (actionKey.Equals("open-data-folder", StringComparison.Ordinal))
        {
            OpenDataFolder();
            return;
        }

        const string Prefix = "navigate:";
        if (actionKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            SettingsNavigationRequested?.Invoke(this, actionKey[Prefix.Length..]);
        }
    }

    public string DoctorReportText =>
        _doctorReport is null ? "" : DiagnosticReportFormatter.Format(_doctorReport);
}

/// <summary>Doctor 结果行；状态字符串用于 XAML DataTrigger，避免在绑定中解析枚举。</summary>
public sealed class DiagnosticItemViewModel
{
    public DiagnosticItemViewModel(DiagnosticResult result)
    {
        Id = result.Id;
        var action = DiagnosticActionCatalog.Resolve(result);
        ActionKey = action?.Key ?? "";
        ActionLabel = action?.Label ?? "";
        Title = result.Title;
        Detail = result.Detail;
        Recommendation = result.Recommendation ?? "";
        StatusKey = result.Status.ToString();
        StatusText = DiagnosticReportFormatter.StatusText(result.Status);
        StatusGlyph = result.Status switch
        {
            DiagnosticStatus.Passed => "\uE73E",
            DiagnosticStatus.Warning => "\uE7BA",
            DiagnosticStatus.Failed => "\uEA39",
            _ => "\uE946",
        };
    }

    public string Id { get; }

    public string Title { get; }

    public string Detail { get; }

    public string Recommendation { get; }

    public string StatusKey { get; }

    public string StatusText { get; }

    public string StatusGlyph { get; }

    public string ActionKey { get; }

    public string ActionLabel { get; }

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionKey);

    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);
}
