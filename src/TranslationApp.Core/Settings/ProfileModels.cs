using System.Text.Encodings.Web;
using System.Text.Json;

namespace TranslationApp.Core.Settings;

/// <summary>
/// 场景档案的键覆盖（FR-037 / spec §3.1）：**稀疏**语义——只有非 null 键参与切换，
/// 档案没指定的键保持用户现值（行为开关不代用户决定）。
/// 键集合固定为这 9 个设置项（批 5 / FR-054 加了 <see cref="Style"/>），
/// 新增键需同步 <see cref="FromSettings"/> / <see cref="ApplyTo"/> /
/// <see cref="DeviatesFrom"/> 三处。
/// </summary>
public sealed class ProfileOverrides
{
    public string? Engine { get; set; }
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public bool? CleanClipboardText { get; set; }
    public bool? PrivacyMode { get; set; }
    public bool? GlossaryEnabled { get; set; }
    public bool? ClipboardMonitorEnabled { get; set; }
    public bool? HoverSelectEnabled { get; set; }

    /// <summary>译文风格（FR-054 新增的第 9 键，取值同 <c>AppSettings.TranslationStyle</c>）。</summary>
    public string? Style { get; set; }

    /// <summary>把当前设置整组快照为固定值（「把当前设置存为档案」用，spec §3.2）。</summary>
    public static ProfileOverrides FromSettings(AppSettings s) => new()
    {
        Engine = s.Engine,
        SourceLanguage = s.SourceLanguage,
        TargetLanguage = s.TargetLanguage,
        CleanClipboardText = s.CleanClipboardText,
        PrivacyMode = s.PrivacyMode,
        GlossaryEnabled = s.GlossaryEnabled,
        ClipboardMonitorEnabled = s.ClipboardMonitorEnabled,
        HoverSelectEnabled = s.HoverSelectEnabled,
        Style = s.TranslationStyle,
    };

    /// <summary>非 null 键写入目标设置。</summary>
    public void ApplyTo(AppSettings s)
    {
        if (Engine is not null) s.Engine = Engine;
        if (SourceLanguage is not null) s.SourceLanguage = SourceLanguage;
        if (TargetLanguage is not null) s.TargetLanguage = TargetLanguage;
        if (CleanClipboardText is { } clean) s.CleanClipboardText = clean;
        if (PrivacyMode is { } privacy) s.PrivacyMode = privacy;
        if (GlossaryEnabled is { } glossary) s.GlossaryEnabled = glossary;
        if (ClipboardMonitorEnabled is { } monitor) s.ClipboardMonitorEnabled = monitor;
        if (HoverSelectEnabled is { } hover) s.HoverSelectEnabled = hover;
        if (Style is not null) s.TranslationStyle = Style;
    }

    /// <summary>任一钉住的键与现值不一致 = 偏离（设置页「当前设置已偏离」标注，spec §3.3）。</summary>
    public bool DeviatesFrom(AppSettings s) =>
        (Engine is not null && Engine != s.Engine)
        || (SourceLanguage is not null && SourceLanguage != s.SourceLanguage)
        || (TargetLanguage is not null && TargetLanguage != s.TargetLanguage)
        || (CleanClipboardText is { } clean && clean != s.CleanClipboardText)
        || (PrivacyMode is { } privacy && privacy != s.PrivacyMode)
        || (GlossaryEnabled is { } glossary && glossary != s.GlossaryEnabled)
        || (ClipboardMonitorEnabled is { } monitor && monitor != s.ClipboardMonitorEnabled)
        || (HoverSelectEnabled is { } hover && hover != s.HoverSelectEnabled)
        || (Style is not null && Style != s.TranslationStyle);
}

/// <summary>一份场景档案：名称 + 键覆盖。自定义档案列表在 <c>AppSettings.CustomProfilesJson</c> 里存本类型的数组。</summary>
public sealed class AppProfile
{
    public string Name { get; set; } = "";
    public ProfileOverrides Overrides { get; set; } = new();
}
