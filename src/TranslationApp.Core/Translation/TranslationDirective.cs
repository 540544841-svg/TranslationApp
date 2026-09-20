namespace TranslationApp.Core.Translation;

/// <summary>
/// 译文风格（FR-051 / 批 5 spec §2）：只有 AI 引擎有对应通道，官方引擎无此能力。
/// 字符串形式持久化在 <c>AppSettings.TranslationStyle</c>（枚举名小写）。
/// </summary>
public enum TranslationStyle
{
    /// <summary>不加风格指令（默认）。</summary>
    None = 0,

    /// <summary>更口语。</summary>
    Colloquial,

    /// <summary>更正式。</summary>
    Formal,

    /// <summary>更简短。</summary>
    Concise,
}

/// <summary>
/// 一次翻译请求附加给引擎的指令（批 5 spec §1.1）：语境 + 风格，二者都可为空。
/// 只有实现 <see cref="IPromptDirectiveTranslator"/> 的引擎会读到它，其余引擎走无指令的老路径。
/// </summary>
public readonly record struct TranslationDirective(string? ContextBefore, TranslationStyle Style)
{
    /// <summary>无指令：与不传 directive 完全等价。</summary>
    public static readonly TranslationDirective None = new(null, TranslationStyle.None);

    public bool IsEmpty => string.IsNullOrEmpty(ContextBefore) && Style == TranslationStyle.None;
}

/// <summary>
/// 引擎是否接受调用方附加的 Prompt 指令（FR-050 / FR-051）。当前只有 AI 引擎实现，
/// 装饰器 <see cref="GlossaryTranslator"/> 原样转发。UI 用它决定「换说法」按钮是否出现——
/// 不支持的引擎绝不假装支持。
/// </summary>
public interface IPromptDirectiveTranslator
{
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationDirective directive,
        CancellationToken cancellationToken = default);
}

/// <summary>风格的界面文案与持久化名（小写枚举名，写进 settings.json 与档案覆盖）。</summary>
public static class TranslationStyles
{
    public static string ToSettingKey(this TranslationStyle style) => style switch
    {
        TranslationStyle.Colloquial => "colloquial",
        TranslationStyle.Formal => "formal",
        TranslationStyle.Concise => "concise",
        _ => "none",
    };

    public static TranslationStyle Parse(string? key) => key switch
    {
        "colloquial" => TranslationStyle.Colloquial,
        "formal" => TranslationStyle.Formal,
        "concise" => TranslationStyle.Concise,
        _ => TranslationStyle.None,
    };

    /// <summary>界面显示名（按钮文案 / 状态行 / 档案标注）。</summary>
    public static string DisplayName(this TranslationStyle style) => style switch
    {
        TranslationStyle.Colloquial => "更口语",
        TranslationStyle.Formal => "更正式",
        TranslationStyle.Concise => "更简短",
        _ => "原样",
    };
}
