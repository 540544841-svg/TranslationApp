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
    private bool _doctorBusy;

    [ObservableProperty]
    private string _doctorMessage = "";

    [ObservableProperty]
    private string _doctorSummary = "尚未运行诊断";

    /// <summary>
    /// 诊断页初始化：先用固定清单把全部检查项铺成「待检查」，App 启动时那次自动诊断
    /// 随后把每一行改成结论——清单不再等跑完才出现，页面也不会在启动后空着。
    /// 启动那次由 App 发起，设置窗口晚于它时直接回填；早于它时靠 ReportReady 补上。
    /// </summary>
    private void InitializeDoctorPage()
    {
        SeedDoctorPlaceholders();
        DoctorSummary = $"待检查 · {DoctorResults.Count} 项";
        _doctor.ReportReady += OnDoctorReportReady;

        if (_doctor.LastReport is { } report)
        {
            ApplyDoctorReport(report);
        }
        else if (_doctor.IsRunning)
        {
            DoctorSummary = "诊断进行中…";
            DoctorMessage = "启动时已自动开始检查热键、OCR、代理、数据库与更新通道…";
        }
    }

    /// <summary>后台线程跑完的诊断：切回 UI 线程再铺清单。</summary>
    private void OnDoctorReportReady(object? sender, DiagnosticReport report)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyDoctorReport(report);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => ApplyDoctorReport(report)));
        }
    }

    /// <summary>
    /// 把一次诊断结果落到界面。启动那次与用户手点的「重新诊断」走同一条路径，
    /// 同一份报告只铺一次（RunAsync 内部会先触发 ReportReady，外层不必再铺）。
    /// </summary>
    private void ApplyDoctorReport(DiagnosticReport report)
    {
        if (ReferenceEquals(report, _doctorReport))
        {
            return;
        }

        _doctorReport = report;
        PublishDoctorResults(report.Results);
        DoctorSummary = report.SummaryText;
        DoctorMessage = $"诊断完成于 {report.GeneratedAtUtc.ToLocalTime():HH:mm:ss}；报告已按脱敏规则生成。";
        NotifyDoctorAggregates();
        OnPropertyChanged(nameof(DoctorReportText));
        CopyDoctorReportCommand.NotifyCanExecuteChanged();
        ExportDoctorReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把固定检查项清单铺成诊断行（状态「待检查」）；诊断进行中也用它复位。</summary>
    private void SeedDoctorPlaceholders()
    {
        var checks = TranslationApp.Services.DoctorService.ExpectedChecks;
        DoctorResults.Clear();
        for (var i = 0; i < checks.Count; i++)
        {
            DoctorResults.Add(new DiagnosticItemViewModel(checks[i].Id, checks[i].Title)
            {
                IsLast = i == checks.Count - 1,
            });
        }

        NotifyDoctorAggregates();
    }

    /// <summary>三个派生量（可修项 / 需处理项）在清单变化后统一广播。</summary>
    private void NotifyDoctorAggregates()
    {
        OnPropertyChanged(nameof(DoctorHasActions));
        OnPropertyChanged(nameof(DoctorIssueCount));
        OnPropertyChanged(nameof(DoctorHasIssues));
    }

    /// <summary>至少有一项检查可以就地修复：决定「修复入口」分组是否出现。</summary>
    public bool DoctorHasActions => DoctorResults.Any(item => item.HasAction);

    /// <summary>需要处理的项数（警告 + 失败），对应「已发现 N 项需要处理」。</summary>
    public int DoctorIssueCount => DoctorResults.Count(item => item.StatusKey is "Warning" or "Failed");

    public bool DoctorHasIssues => DoctorIssueCount > 0;

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

        // 启动那次还在跑：等它自己的 ReportReady，不并发开第二遍（子进程与联网探测都只该做一次）
        if (_doctor.IsRunning)
        {
            return;
        }

        DoctorBusy = true;
        SeedDoctorPlaceholders();
        _doctorReport = null;
        DoctorSummary = "诊断进行中…";
        DoctorMessage = "正在检查热键、OCR、代理、数据库与更新通道…";
        OnPropertyChanged(nameof(DoctorReportText));
        CopyDoctorReportCommand.NotifyCanExecuteChanged();
        ExportDoctorReportCommand.NotifyCanExecuteChanged();

        try
        {
            ApplyDoctorReport(await _doctor.RunAsync());
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

    /// <summary>
    /// 把一次诊断的结果铺进清单：末行去掉底部发丝线，并标出「修复入口」里最后一个可执行项，
    /// 两处都靠这两个标记避免与外框重叠成两像素。
    /// </summary>
    private void PublishDoctorResults(IEnumerable<DiagnosticResult> results)
    {
        DoctorResults.Clear();
        var items = results.Select(result => new DiagnosticItemViewModel(result)).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].IsLast = i == items.Count - 1;
        }

        var lastAction = items.FindLastIndex(item => item.HasAction);
        if (lastAction >= 0)
        {
            items[lastAction].IsLastAction = true;
        }

        foreach (var item in items)
        {
            DoctorResults.Add(item);
        }
    }

    /// <summary>
    /// 仅离屏渲染（--render-ui）使用：注入一组与设计稿同构的自检结果，
    /// 让 QA 出图能看到清单版面，而不必在无人值守环境真的联网跑一次诊断。
    /// </summary>
    internal void SeedDoctorResultsForRender(IReadOnlyList<DiagnosticResult> results)
    {
        PublishDoctorResults(results);
        DoctorSummary = "刚刚跑过 · 3.2s";
        DoctorMessage = "";
        NotifyDoctorAggregates();
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
                Title = "导出译印诊断报告",
                Filter = "文本报告 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = $"译印诊断-{DateTime.Now:yyyyMMdd-HHmm}.txt",
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
    /// <summary>
    /// 「待检查」占位行：应用启动时就按固定清单铺出来，跑完由带结果的那次构造覆盖。
    /// 占位行的 StatusKey 是 Pending，XAML 里没有任何 DataTrigger 匹配它，
    /// 落到基样式（阴刻灰底 + 次级文字），正好就是「还没跑」该有的样子。
    /// </summary>
    public DiagnosticItemViewModel(string id, string title)
    {
        Id = id;
        ActionKey = "";
        ActionLabel = "";
        Title = title;
        Detail = "等待检查…";
        Recommendation = "";
        StatusKey = "Pending";
        StatusText = "待检查";
        StatusGlyph = "\uE946";
    }

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

    /// <summary>清单末行（设计稿 .row:last-child）：XAML 用它去掉底部发丝线。</summary>
    public bool IsLast { get; set; }

    /// <summary>「修复入口」里最后一个可执行项，规则同上。</summary>
    public bool IsLastAction { get; set; }

    public bool HasAction => !string.IsNullOrWhiteSpace(ActionKey);

    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);
}
