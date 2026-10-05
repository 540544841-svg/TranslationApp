using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Updates;

namespace TranslationApp.ViewModels;

/// <summary>签名更新、便携模式与备份恢复。</summary>
public partial class SettingsViewModel
{
    private UpdateManifest? _availableUpdate;

    [ObservableProperty]
    private string _updateManifestUrl = "";

    [ObservableProperty]
    private bool _updateAutoCheck;

    [ObservableProperty]
    private string _updateMessage = "";

    [ObservableProperty]
    private bool _updateBusy;

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private bool _portableMode;

    private bool _suppressPortableCallback;

    /// <summary>便携开关是否可用：EXE 目录可写才允许把印谱搬过去（Program Files 不行）。</summary>
    public bool PortableAvailable => AppPaths.CanWriteBaseDirectory;

    /// <summary>为什么落在这里——把自动推断的结果讲清楚，用户不必做选择题。</summary>
    public string PortableNote =>
        !AppPaths.CanWriteBaseDirectory
            ? "EXE 所在目录不可写，固定写在系统用户目录"
            : AppPaths.IsPortable
                ? "印谱写在 EXE 同目录，整个文件夹拷走即可迁移"
                : "印谱写在系统用户目录；放进 U 盘会自动改为随身";

    /// <summary>刚改过开关、要重启才切过去——只有这时候才在开关旁挂「重启生效」角标。</summary>
    public bool PortableRestartPending => AppPaths.PortableRestartPending;

    [ObservableProperty]
    private string _backupMessage = "";

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (UpdateBusy) return;
        UpdateBusy = true;
        UpdateMessage = "检查中…";
        try
        {
            var result = await _updates.CheckAsync(UpdateManifestUrl, CurrentAppVersion());
            UpdateMessage = result.Message;
            _availableUpdate = result.HasUpdate ? result.Manifest : null;
            UpdateAvailable = _availableUpdate is not null;
            _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查更新失败");
            UpdateMessage = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (UpdateBusy || _availableUpdate is null) return;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            UpdateMessage = "开发运行模式不支持自动替换，请使用发布版 EXE 验证更新。";
            return;
        }

        UpdateBusy = true;
        UpdateProgress = 0;
        UpdateMessage = "正在下载…";
        try
        {
            var progress = new Progress<double>(value => UpdateProgress = value);
            var download = await _updates.DownloadAsync(
                _availableUpdate, AppPaths.UpdatesDirectory, progress);
            if (!download.Success || download.FilePath is null)
            {
                UpdateMessage = download.Message;
                return;
            }

            // 候选文件先复制到目标 EXE 同目录，最终替换才能是同卷 Move；不能放 AppData 更新缓存。
            var plan = UpdateInstaller.Prepare(download.FilePath, processPath);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{plan.ScriptPath}\"",
            };
            Process.Start(start);
            UpdateMessage = "安装脚本已启动；应用将退出，失败时自动恢复上一版本。";
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载或安装更新失败");
            UpdateMessage = $"更新失败：{ex.Message}";
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private void CreateBackup()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出译印备份",
            Filter = "译印备份 (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = $"译印备份-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = AppPaths.BackupsDirectory,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var manifest = _backup.BackupTo(dialog.FileName, CurrentAppVersion().ToString());
            BackupMessage = $"已备份 {manifest.Files.Count} 个数据文件：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "备份失败");
            BackupMessage = $"备份失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择译印备份",
            Filter = "译印备份 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            InitialDirectory = AppPaths.BackupsDirectory,
        };
        if (dialog.ShowDialog() != true) return;

        var inspection = _backup.Inspect(dialog.FileName);
        if (!inspection.IsValid)
        {
            BackupMessage = inspection.Message;
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            "恢复会替换当前设置、历史与生词本。建议先导出当前数据，是否继续？",
            "译印 · 恢复备份",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;

        var result = _backup.RestoreFrom(dialog.FileName);
        BackupMessage = result.IsValid ? $"{result.Message}；重启译印后完整生效" : result.Message;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BackupMessage = $"无法打开数据目录：{ex.Message}";
        }
    }

    partial void OnUpdateManifestUrlChanged(string value) => Save(s => s.UpdateManifestUrl = value.Trim());

    partial void OnUpdateAutoCheckChanged(bool value) => Save(s => s.UpdateAutoCheck = value);

    partial void OnPortableModeChanged(bool value)
    {
        if (_suppressPortableCallback)
        {
            return;
        }

        try
        {
            AppPaths.SetPortable(value);
            BackupMessage = value
                ? "已启用便携模式；重启后印谱改写到 EXE 同目录 data"
                : "已关闭便携模式；重启后印谱回到系统用户目录";
        }
        catch (Exception ex)
        {
            // SetPortable 的消息已经是给用户看的，别再包一层「失败：」把信息盖掉。
            Log.Warning(ex, "切换便携模式失败");
            _suppressPortableCallback = true;
            PortableMode = AppPaths.IsPortable;
            _suppressPortableCallback = false;
            BackupMessage = ex.Message;
        }

        OnPropertyChanged(nameof(PortableRestartPending));
    }

    private static ReleaseVersion CurrentAppVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0, 0, 0);
        return new ReleaseVersion(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }
    /// <summary>设置页「版本」行展示的当前版本号（设计稿 §02 版本行）。</summary>
    public string AppVersionText
    {
        get
        {
            var v = CurrentAppVersion();
            return $"v{v.Major}.{v.Minor}.{v.Patch}";
        }
    }

    /// <summary>当前数据目录（随便携模式实时变化），供设置页只读展示。</summary>
    public string DataFolderPath => AppPaths.DataDirectory;
}
