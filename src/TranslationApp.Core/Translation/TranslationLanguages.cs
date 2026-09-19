namespace TranslationApp.Core.Translation;

/// <summary>
/// 语言选项（内部语言码 = Google 翻译语言码）。
/// 常用语言置顶：中/英/日/韩/法/德/俄/西（FR-007）。
/// </summary>
public static class TranslationLanguages
{
    public sealed record LanguageOption(string Code, string Display);

    public const string AutoCode = "auto";

    // 注意：必须先于 SourceOptions 声明（静态字段按声明顺序初始化）
    private static readonly LanguageOption[] Common =
    [
        new("zh-CN", "中文（简体）"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("ru", "俄语"),
        new("es", "西班牙语"),
    ];

    /// <summary>源语言列表：自动检测置顶 + 常用语言。</summary>
    public static readonly LanguageOption[] SourceOptions =
    [
        new(AutoCode, "自动检测"),
        ..Common,
    ];

    /// <summary>目标语言列表（不含自动检测）。</summary>
    public static readonly LanguageOption[] TargetOptions = Common;

    /// <summary>按语言码取显示名（未知码原样返回，检测语言回填时兜底）。</summary>
    public static string DisplayName(string code) =>
        code == AutoCode ? "自动检测" : Common.FirstOrDefault(o => o.Code == code)?.Display ?? code;
}
