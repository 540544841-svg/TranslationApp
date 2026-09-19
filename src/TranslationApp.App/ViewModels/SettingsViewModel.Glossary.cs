using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;

namespace TranslationApp.ViewModels;

/// <summary>
/// 「术语表」页（P0 批 1 / spec §1.4）：词条编辑即时序列化落盘（配置即时保存纪律的延续），
/// 运行时由 GlossaryCache 按 JSON 变化失效，翻译链路无需重启即生效。
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>术语表页的一行词条（编辑即保存；Enabled=false 停用但保留）。</summary>
    public sealed partial class GlossaryItemViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _source = "";

        [ObservableProperty]
        private string _target = "";

        [ObservableProperty]
        private bool _enabled = true;
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
            });
        }
        _suppressGlossarySave = false;

        GlossaryItems.CollectionChanged += OnGlossaryCollectionChanged;
        HookGlossaryItemEvents();
    }

    private void OnGlossaryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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
        GlossaryMessage = "新词条已添加，填好源词与译法即生效";
    }

    [RelayCommand]
    private void DeleteGlossary(GlossaryItemViewModel? item)
    {
        if (item is null) return;
        GlossaryItems.Remove(item); // 移除即触发保存
        GlossaryMessage = "已删除词条";
    }

    /// <summary>序列化并落盘；空源词/空译法的行由 Parse 过滤，不写入配置。</summary>
    private void SaveGlossary(bool quiet)
    {
        if (_suppressGlossarySave) return;

        var items = GlossaryItems
            .Where(i => !string.IsNullOrWhiteSpace(i.Source) && !string.IsNullOrWhiteSpace(i.Target))
            .Select(i => new GlossaryItem(i.Source.Trim(), i.Target.Trim(), i.Enabled))
            .ToList();

        Save(s => s.GlossaryJson = GlossaryReplacer.Serialize(items));
        GlossaryCorrupted = false;
        if (!quiet)
        {
            GlossaryMessage = $"已保存 · 生效 {items.Count} 条";
        }
    }
}
