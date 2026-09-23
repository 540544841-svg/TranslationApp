using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>
/// 「术语表」页：编辑即时落盘，运行时由 GlossaryCache 按 JSON 变化失效；
/// v2 增加语言对作用域、匹配方式和 CSV/TSV 导入导出。
/// </summary>
public partial class SettingsViewModel
{
    public sealed record GlossaryLanguageOption(string Code, string Display);

    public sealed record GlossaryMatchOption(string Value, string Display);

    public static IReadOnlyList<GlossaryLanguageOption> GlossarySourceLanguageOptions { get; } =
    [
        new("*", "全部源语言"),
        .. TranslationLanguages.SourceOptions
            .Where(option => option.Code != TranslationLanguages.AutoCode)
            .Select(option => new GlossaryLanguageOption(option.Code, option.Display)),
    ];

    public static IReadOnlyList<GlossaryLanguageOption> GlossaryTargetLanguageOptions { get; } =
    [
        new("*", "全部目标语言"),
        .. TranslationLanguages.TargetOptions
            .Select(option => new GlossaryLanguageOption(option.Code, option.Display)),
    ];

    public static IReadOnlyList<GlossaryMatchOption> GlossaryMatchOptions { get; } =
    [
        new(GlossaryMatchModes.Word, "整词 / 词边界"),
        new(GlossaryMatchModes.Contains, "包含片段"),
        new(GlossaryMatchModes.CaseSensitive, "区分大小写"),
        new(GlossaryMatchModes.Regex, "正则表达式"),
    ];

    /// <summary>术语表页的一行词条（编辑即保存；Enabled=false 停用但保留）。</summary>
    public sealed partial class GlossaryItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _source = "";

        [ObservableProperty]
        private string _target = "";

        [ObservableProperty]
        private bool _enabled = true;

        [ObservableProperty]
        private string _sourceLanguage = "*";

        [ObservableProperty]
        private string _targetLanguage = "*";

        [ObservableProperty]
        private string _matchMode = GlossaryMatchModes.Word;

        [ObservableProperty]
        private string _note = "";

        public GlossaryItem ToModel() => new(
            Source.Trim(),
            Target.Trim(),
            Enabled,
            string.IsNullOrWhiteSpace(SourceLanguage) ? "*" : SourceLanguage.Trim(),
            string.IsNullOrWhiteSpace(TargetLanguage) ? "*" : TargetLanguage.Trim(),
            GlossaryMatchModes.Normalize(MatchMode),
            Note.Trim());
    }

    /// <summary>术语词条（绑定术语表页 ItemsControl）。</summary>
    public ObservableCollection<GlossaryItemViewModel> GlossaryItems { get; } = [];

    /// <summary>配置文件里的 GlossaryJson 损坏（手改），已按空表运行。</summary>
    [ObservableProperty]
    private bool _glossaryCorrupted;

    /// <summary>保存结果 / 上限提示。</summary>
    [ObservableProperty]
    private string _glossaryMessage = "";

    private bool _suppressGlossarySave;

    private void InitializeGlossaryPage()
    {
        var items = GlossaryReplacer.Parse(_settings.GlossaryJson, out var corrupted);
        GlossaryCorrupted = corrupted;

        _suppressGlossarySave = true;
        foreach (var item in items)
        {
            GlossaryItems.Add(new GlossaryItemViewModel
            {
                Source = item.Source,
                Target = item.Target,
                Enabled = item.Enabled,
                SourceLanguage = string.IsNullOrWhiteSpace(item.SourceLanguage) ? "*" : item.SourceLanguage,
                TargetLanguage = string.IsNullOrWhiteSpace(item.TargetLanguage) ? "*" : item.TargetLanguage,
                MatchMode = GlossaryMatchModes.Normalize(item.MatchMode),
                Note = item.Note ?? "",
            });
        }
        _suppressGlossarySave = false;

        GlossaryItems.CollectionChanged += OnGlossaryCollectionChanged;
        HookGlossaryItemEvents();
    }

    private void OnGlossaryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (GlossaryItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnGlossaryItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (GlossaryItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnGlossaryItemPropertyChanged;
            }
        }

        SaveGlossary(quiet: true);
    }

    private void HookGlossaryItemEvents()
    {
        foreach (var item in GlossaryItems)
        {
            item.PropertyChanged += OnGlossaryItemPropertyChanged;
        }
    }

    private void OnGlossaryItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GlossaryItemViewModel) return;
        SaveGlossary(quiet: true);
    }

    [RelayCommand]
    private void AddGlossary()
    {
        if (GlossaryItems.Count >= GlossaryReplacer.MaxItems)
        {
            GlossaryMessage = $"已达 {GlossaryReplacer.MaxItems} 条上限，先删除旧词条";
            return;
        }

        var item = new GlossaryItemViewModel();
        GlossaryItems.Add(item); // CollectionChanged 里订阅事件并保存
        GlossaryMessage = "新词条已添加；默认作用于全部语言、整词匹配";
    }

    [RelayCommand]
    private void DeleteGlossary(GlossaryItemViewModel? item)
    {
        if (item is null) return;
        GlossaryItems.Remove(item); // 移除即触发保存
        GlossaryMessage = "已删除词条";
    }

    [RelayCommand]
    private void ImportGlossary()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入术语表",
            Filter = "术语表 (*.csv;*.tsv)|*.csv;*.tsv|CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var extension = Path.GetExtension(dialog.FileName);
            var delimiter = string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            var preview = GlossaryFileCodec.Parse(
                File.ReadAllText(dialog.FileName),
                delimiter,
                GlossaryItems.Select(item => item.ToModel()).ToList());

            var capacity = Math.Max(0, GlossaryReplacer.MaxItems - GlossaryItems.Count);
            var accepted = preview.Items.Take(capacity).ToList();
            var notImported = preview.Items.Count - accepted.Count;
            if (accepted.Count == 0 && preview.Warnings.Count == 0 && notImported == 0)
            {
                GlossaryMessage = "文件中没有可导入的词条";
                return;
            }

            var summary = new StringBuilder();
            summary.AppendLine($"将导入 {accepted.Count} 条；现有 {GlossaryItems.Count} 条。");
            if (notImported > 0)
            {
                summary.AppendLine($"另有 {notImported} 条因达到 {GlossaryReplacer.MaxItems} 条上限不会导入。");
            }

            foreach (var warning in preview.Warnings.Take(8))
            {
                summary.AppendLine("• " + warning);
            }

            if (preview.Warnings.Count > 8)
            {
                summary.AppendLine($"• 另有 {preview.Warnings.Count - 8} 条提示未展开");
            }

            summary.Append("是否继续导入？");
            var confirm = System.Windows.MessageBox.Show(
                summary.ToString(),
                "速译 · 导入术语表",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Information);
            if (confirm != System.Windows.MessageBoxResult.OK) return;

            _suppressGlossarySave = true;
            try
            {
                foreach (var item in accepted)
                {
                    GlossaryItems.Add(new GlossaryItemViewModel
                    {
                        Source = item.Source,
                        Target = item.Target,
                        Enabled = item.Enabled,
                        SourceLanguage = item.SourceLanguage,
                        TargetLanguage = item.TargetLanguage,
                        MatchMode = GlossaryMatchModes.Normalize(item.MatchMode),
                        Note = item.Note,
                    });
                }
            }
            finally
            {
                _suppressGlossarySave = false;
            }

            SaveGlossary(quiet: false);
            GlossaryMessage = $"导入完成：新增 {accepted.Count} 条，跳过 {preview.DuplicateCount} 条重复项";
        }
        catch (Exception ex)
        {
            GlossaryMessage = $"导入失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportGlossary()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出术语表",
            Filter = "CSV (*.csv)|*.csv|TSV (*.tsv)|*.tsv",
            DefaultExt = ".csv",
            FileName = $"速译术语表-{DateTime.Now:yyyyMMdd}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var useTsv = string.Equals(Path.GetExtension(dialog.FileName), ".tsv", StringComparison.OrdinalIgnoreCase);
            var content = useTsv
                ? GlossaryFileCodec.ExportTsv(GlossaryItems.Select(item => item.ToModel()).ToList())
                : GlossaryFileCodec.ExportCsv(GlossaryItems.Select(item => item.ToModel()).ToList());
            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            GlossaryMessage = $"已导出 {GlossaryItems.Count} 条到 {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            GlossaryMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>序列化并落盘；空源词/空译法的行由 Parse 过滤，不写入配置。</summary>
    private void SaveGlossary(bool quiet)
    {
        if (_suppressGlossarySave) return;

        var items = GlossaryItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.Target))
            .Select(item => item.ToModel())
            .Take(GlossaryReplacer.MaxItems)
            .ToList();

        Save(s => s.GlossaryJson = GlossaryReplacer.Serialize(items));
        GlossaryCorrupted = false;
        if (!quiet)
        {
            GlossaryMessage = $"已保存 · 生效 {items.Count} 条";
        }
    }
}
