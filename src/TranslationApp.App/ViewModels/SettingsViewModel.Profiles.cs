using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TranslationApp.Core.Settings;

namespace TranslationApp.ViewModels;

/// <summary>「场景档案」卡片里的自定义档案行（内置两档固定显示，不进列表）。</summary>
public sealed class ProfileRowViewModel
{
    public required string Name { get; init; }
}

/// <summary>
/// 设置页「通用 → 场景档案」卡片（FR-037 / spec §3.4）：内置两档一键应用、
/// 当前设置快照为自定义档案、删除，以及「已偏离」标注。
/// 切换后把 VM 显示字段从 settings 回填（直接改 backing field，避免触发 OnChanged 的重复落盘与副作用）。
/// </summary>
public partial class SettingsViewModel
{
    public ObservableCollection<ProfileRowViewModel> ProfileRows { get; } = [];

    /// <summary>「当前档案：X（当前设置已偏离）」。</summary>
    [ObservableProperty]
    private string _profileStatusText = "";

    /// <summary>「存为档案」输入框的名称。</summary>
    [ObservableProperty]
    private string _newProfileName = "";

    private void InitializeProfilePage()
    {
        RefreshProfileUi();
    }

    [RelayCommand]
    private void ApplyProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _profiles.Apply(name);
        _store.Save(_settings);
        ApplySideEffectsAfterProfileSwitch();
        ReloadSettingsDisplay();
        RefreshProfileUi();
        Log.Information("设置页切换场景档案：{Profile}", _profiles.DisplayName);
    }

    [RelayCommand]
    private void SaveCurrentAsProfile()
    {
        if (_profiles.SaveCurrentAs(NewProfileName))
        {
            _store.Save(_settings);
            NewProfileName = "";
            RefreshProfileUi();
            return;
        }

        ProfileStatusText = $"保存失败：「{NewProfileName.Trim()}」为空、与现有档案重名、超 {ProfileService.MaxProfileNameLength} 字或档案已达 {ProfileService.MaxCustomProfiles} 个";
    }

    [RelayCommand]
    private void DeleteProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_profiles.DeleteCustom(name))
        {
            return;
        }

        _store.Save(_settings);
        ReloadSettingsDisplay();
        RefreshProfileUi();
    }

    /// <summary>档案可能改过隐私/监听/悬停/清洗/术语/引擎/语言——按最新值重放副作用门控。</summary>
    private void ApplySideEffectsAfterProfileSwitch()
    {
        if (_settings.PrivacyMode)
        {
            _clipboardMonitor.Stop();
        }
        else if (_settings.ClipboardMonitorEnabled)
        {
            _clipboardMonitor.Start();
        }

        ApplyHookGates();
    }

    /// <summary>把档案改动的键回填到各页显示字段（只发通知、不走属性 setter，避免二次落盘/递归副作用）。</summary>
    private void ReloadSettingsDisplay()
    {
        // 有意绕开属性 setter：setter 会触发 OnXxxChanged 的落盘与副作用重放（档案切换已经做过一遍）
#pragma warning disable MVVMTK0034
        _privacyMode = _settings.PrivacyMode;
        OnPropertyChanged(nameof(PrivacyMode));
        OnPropertyChanged(nameof(HoverLockedByPrivacy));

        _clipboardMonitorEnabled = _settings.ClipboardMonitorEnabled;
        OnPropertyChanged(nameof(ClipboardMonitorEnabled));

        _hoverSelectEnabled = _settings.HoverSelectEnabled;
        OnPropertyChanged(nameof(HoverSelectEnabled));

        _cleanClipboardText = _settings.CleanClipboardText;
        OnPropertyChanged(nameof(CleanClipboardText));

        _glossaryEnabled = _settings.GlossaryEnabled;
        OnPropertyChanged(nameof(GlossaryEnabled));

        _targetLanguage = _settings.TargetLanguage;
        OnPropertyChanged(nameof(TargetLanguage));

        _selectedEngine = _catalog.Resolve(_settings.Engine).Id;
        OnPropertyChanged(nameof(SelectedEngine));

        _ankiEnabled = _settings.AnkiEnabled;
        OnPropertyChanged(nameof(AnkiEnabled));

        _doubleTapTranslateEnabled = _settings.DoubleTapTranslateEnabled;
        OnPropertyChanged(nameof(DoubleTapTranslateEnabled));
        _doubleTapKey = NormalizeDoubleTapKey(_settings.DoubleTapKey);
        OnPropertyChanged(nameof(DoubleTapKey));
        _mouseSideButtonSelect = _settings.MouseSideButtonSelect;
        OnPropertyChanged(nameof(MouseSideButtonSelect));
        _mouseSideButtonCapture = _settings.MouseSideButtonCapture;
        OnPropertyChanged(nameof(MouseSideButtonCapture));
        _pasteTranslateEnabled = _settings.PasteTranslateEnabled;
        OnPropertyChanged(nameof(PasteTranslateEnabled));
#pragma warning restore MVVMTK0034
    }

    private void RefreshProfileUi()
    {
        ProfileRows.Clear();
        foreach (var profile in _profiles.CustomProfiles())
        {
            ProfileRows.Add(new ProfileRowViewModel { Name = profile.Name });
        }

        ProfileStatusText = $"当前档案：{_profiles.DisplayName}"
            + (_profiles.IsDeviation() ? "（当前设置已偏离）" : "");
    }
}
