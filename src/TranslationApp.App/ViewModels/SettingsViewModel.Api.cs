using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using TranslationApp.Core.Api;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Dictionary;
using TranslationApp.Services;

namespace TranslationApp.ViewModels;

/// <summary>
/// 设置页「高级」批 4 两卡：本地 HTTP API（FR-046）与批量识别（FR-047）。
/// API 的启停全部收敛到 <see cref="LocalApiGateway.Apply"/>：任何字段变更 = 重建监听；
/// 隐私模式开启时不监听（与钩子同纪律，门控在传入 Apply 的 allowed 参数里）。
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _localApiEnabled;

    [ObservableProperty]
    private int _localApiPort = LocalApiServer.DefaultPort;

    /// <summary>监听状态行（含启动失败原因）。</summary>
    [ObservableProperty]
    private string _localApiStatusText = "";

    /// <summary>token 掩码显示（前 4 位 + 圆点）；明文只经「复制」按钮进剪贴板。</summary>
    [ObservableProperty]
    private string _localApiTokenMasked = "";

    [ObservableProperty]
    private string _batchOcrStatusText = "";

    [ObservableProperty]
    private bool _isBatchOcrBusy;

    private void InitializeApiPage()
    {
        // 构造期直写 backing field：避免触发 OnChanged 的重复落盘与监听重启
#pragma warning disable MVVMTK0034
        _localApiEnabled = _settings.LocalApiEnabled;
        _localApiPort = _settings.LocalApiPort;
#pragma warning restore MVVMTK0034
        UpdateLocalApiStatus();
    }

    // ---------------- FR-049：本地 mdx 词典 ----------------

    private DictionaryManager? _dictionaries;

    /// <summary>词典列表项（不可用项直接把原因写在第二行，不静默隐藏用户的词典）。</summary>
    public sealed partial class DictionaryRow : ObservableObject
    {
        private readonly DictionaryInfo _info;

        public DictionaryRow(DictionaryInfo info) => _info = info;

        public string FileName => _info.FileName;

        public string Title => _info.IsAvailable
            ? $"{_info.DisplayName}（{_info.WordCount:N0} 条）"
            : _info.FileName;

        public string Subtitle => _info.IsAvailable ? _info.FileName : _info.Reason ?? "无法解析";

        public bool IsAvailable => _info.IsAvailable;

        /// <summary>释义全文（测试查询的结果，展开态才占高度）。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Shown))]
        private string? _definition;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Shown))]
        [NotifyPropertyChangedFor(nameof(IsExpandedVisible))]
        private bool _isExpanded;

        public bool IsExpandedVisible => IsExpanded && Definition is { Length: > 0 };

        /// <summary>折叠时只显示前 120 字。</summary>
        public string Shown => Definition is { Length: > 120 } text && !IsExpanded
            ? text[..120] + "…"
            : Definition ?? "";

        public void SetDefinition(string? text)
        {
            Definition = text;
            IsExpanded = false;
        }

        [RelayCommand]
        private void ToggleExpanded() => IsExpanded = !IsExpanded;
    }

    public ObservableCollection<DictionaryRow> DictionaryItems { get; } = [];

    [ObservableProperty]
    private bool _dictionariesEnabled;

    [ObservableProperty]
    private string _dictionaryTestWord = "";

    [ObservableProperty]
    private string _dictionaryStatusText = "";

    private void InitializeDictionariesPage()
    {
#pragma warning disable MVVMTK0034
        _dictionariesEnabled = _settings.DictionariesEnabled;
#pragma warning restore MVVMTK0034
        RefreshDictionaryList();
    }

    partial void OnDictionariesEnabledChanged(bool value)
    {
        Save(s => s.DictionariesEnabled = value);
        DictionaryStatusText = value ? "" : "词典功能已关闭（词典文件保留，小窗不再查词）";
    }

    private void RefreshDictionaryList()
    {
        DictionaryItems.Clear();
        if (_dictionaries is null)
        {
            return;
        }

        foreach (var info in _dictionaries.List())
        {
            DictionaryItems.Add(new DictionaryRow(info));
        }

        DictionaryStatusText = DictionaryItems.Count == 0
            ? "还没有词典：点「导入词典」选择 .mdx 文件（仅支持 MDX v3-SQLite）"
            : $"共 {DictionaryItems.Count} 份词典，按列表顺序取首个命中";
    }

    [RelayCommand]
    private void ImportDictionary()
    {
        if (_dictionaries is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 mdx 词典",
            Filter = "MDX 词典 (*.mdx)|*.mdx|所有文件 (*.*)|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 解析可能要解压几十 MB 的 SQLite 资源块，别把设置页冻住
        Task.Run(() =>
        {
            var result = _dictionaries.Import(dialog.FileName);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                DictionaryStatusText = result.Ok
                    ? $"已导入 {result.FileName}"
                    : $"导入失败：{result.Reason}";
                RefreshDictionaryList();
            });
        });
    }

    [RelayCommand]
    private void DeleteDictionary(DictionaryRow? row)
    {
        if (_dictionaries is null || row is null)
        {
            return;
        }

        if (!_dictionaries.Delete(row.FileName))
        {
            DictionaryStatusText = $"删除失败：{row.FileName}";
            return;
        }

        RefreshDictionaryList();
    }

    /// <summary>测试查询：对每份可用词典各查一次，看清「哪份命中、命中在哪个词头」。</summary>
    [RelayCommand]
    private async Task TestDictionaryLookupAsync()
    {
        if (_dictionaries is null)
        {
            return;
        }

        var word = (DictionaryTestWord ?? "").Trim();
        if (word.Length == 0)
        {
            DictionaryStatusText = "先输入要测试的词";
            return;
        }

        var hits = await Task.Run(() =>
        {
            var map = new Dictionary<string, string?>();
            foreach (var row in DictionaryItems.Where(r => r.IsAvailable))
            {
                map[row.FileName] = _dictionaries.QueryFrom(row.FileName, word)?.Definition;
            }

            return map;
        });

        var any = false;
        foreach (var row in DictionaryItems)
        {
            row.SetDefinition(hits.TryGetValue(row.FileName, out var text) ? text : null);
            any |= row.Definition is { Length: > 0 };
        }

        DictionaryStatusText = any ? $"「{word}」命中 {hits.Count(h => h.Value is { Length: > 0})} 份词典" : $"「{word}」没有命中";
    }

    partial void OnLocalApiEnabledChanged(bool value)
    {
        Save(s => s.LocalApiEnabled = value);
        RefreshLocalApi();
    }

    partial void OnLocalApiPortChanged(int value)
    {
        var clamped = Math.Clamp(value, 1024, 65535);
        if (clamped != value)
        {
            LocalApiPort = clamped; // 回显夹取值；再次触发本方法完成保存
            return;
        }

        Save(s => s.LocalApiPort = clamped);
        RefreshLocalApi();
    }

    private void RefreshLocalApi() => _localApiGateway.Apply(_settings.LocalApiEnabled && !_settings.PrivacyMode);

    /// <summary>状态行由网关实况生成（VM 不缓存监听状态）。</summary>
    private void UpdateLocalApiStatus()
    {
        if (!_settings.LocalApiEnabled)
        {
            LocalApiStatusText = "未启用";
            LocalApiTokenMasked = "";
            return;
        }

        if (_settings.PrivacyMode)
        {
            LocalApiStatusText = "隐私模式开启中，未监听";
            LocalApiTokenMasked = "";
            return;
        }

        if (_localApiGateway.IsRunning)
        {
            LocalApiStatusText = $"正在监听 http://127.0.0.1:{_settings.LocalApiPort}/（POST /api/translate，仅本机）";
            var token = _localApiGateway.Token ?? "";
            LocalApiTokenMasked = token.Length >= 4 ? token[..4] + "·" + new string('•', 12) : "已启用";
        }
        else
        {
            LocalApiStatusText = _localApiGateway.LastStartFailure ?? "未监听";
            LocalApiTokenMasked = "";
        }
    }

    [RelayCommand]
    private void CopyLocalApiToken()
    {
        var token = _localApiGateway.Token;
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        try
        {
            _clipboardMonitor.Suppress(TimeSpan.FromSeconds(1));
            Clipboard.SetText(token);
            LocalApiStatusText += " · token 已复制（给调用方设置请求头 X-Auth）";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制 API token 失败");
        }
    }

    [RelayCommand]
    private void RegenerateLocalApiToken()
    {
        _localApiGateway.RegenerateToken();
        RefreshLocalApi();
        UpdateLocalApiStatus();
        LocalApiStatusText += " · token 已重新生成，旧 token 立即失效";
    }

    // ==================== FR-047 批量识别 ====================

    /// <summary>
    /// 批量识别（仅图片，PDF 需先转图——无第三方解析依赖）：多选文件 → 逐张解码走现有
    /// OCR 引擎路由（paddle/windows 与截图翻译同一链路）→ 导出 Markdown。单张失败不中断。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBatchOcr))]
    private async Task StartBatchOcrAsync()
    {
        if (!_ocr.IsAvailable)
        {
            BatchOcrStatusText = "系统未安装 OCR 语言包，批量识别不可用";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要识别的图片（可多选）",
            Multiselect = true,
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var files = dialog.FileNames.Where(BatchOcrRunner.IsSupportedImage).ToArray();
        if (files.Length == 0)
        {
            BatchOcrStatusText = "所选文件里没有支持的图片（PDF 请先转成图片）";
            return;
        }

        IsBatchOcrBusy = true;
        BatchOcrStatusText = $"识别中 0 / {files.Length}…";
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => BatchOcrStatusText = $"识别中 {p.Done} / {p.Total}…");
            var items = await Task.Run(() => BatchOcrRunner.RunAsync(files, RecognizeImageFileAsync, progress));
            var okCount = items.Count(i => i.Ok);

            var save = new SaveFileDialog
            {
                Title = "导出识别结果",
                Filter = "Markdown|*.md",
                FileName = $"OCR批量结果-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            };
            if (save.ShowDialog() == true)
            {
                File.WriteAllText(save.FileName, BatchOcrRunner.BuildMarkdown(items), new UTF8Encoding(true));
                BatchOcrStatusText = $"完成：成功 {okCount} / 失败 {items.Count - okCount}，已导出 {Path.GetFileName(save.FileName)}";
            }
            else
            {
                BatchOcrStatusText = $"完成：成功 {okCount} / 失败 {items.Count - okCount}（未导出）";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量识别失败");
            BatchOcrStatusText = "批量识别失败，详见日志";
        }
        finally
        {
            IsBatchOcrBusy = false;
        }
    }

    private bool CanRunBatchOcr() => !IsBatchOcrBusy;

    partial void OnIsBatchOcrBusyChanged(bool value) => StartBatchOcrCommand.NotifyCanExecuteChanged();

    private async Task<string?> RecognizeImageFileAsync(string path)
    {
        var image = ImageFileDecoder.Decode(path, _ocr.MaxImageDimension);
        var languageTag = _settings.OcrLanguage == OcrLanguages.Auto ? null : _settings.OcrLanguage;
        return await _ocr.RecognizeAsync(image.Bgra, image.Width, image.Height, languageTag);
    }
}
