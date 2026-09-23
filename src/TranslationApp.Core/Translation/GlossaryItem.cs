namespace TranslationApp.Core.Translation;

/// <summary>
/// 术语表词条（P0 批 1 / spec §1.1）：译文生成后把 <see cref="Source"/> 统一替换为
/// <see cref="Target"/>，对全部引擎生效；仅存于本地配置，不上传。
/// </summary>
/// <param name="Source">译文里出现的源词（多为英文原词或固定写法）。</param>
/// <param name="Target">期望的固定译法。</param>
/// <param name="Enabled">false 时跳过该词条（保留但不生效）。</param>
/// <param name="SourceLanguage">源语言作用域；* 表示全部。</param>
/// <param name="TargetLanguage">目标语言作用域；* 表示全部。</param>
/// <param name="MatchMode">匹配方式：word / contains / caseSensitive / regex。</param>
/// <param name="Note">用户备注，不参与匹配。</param>
public sealed record GlossaryItem(
    string Source,
    string Target,
    bool Enabled = true,
    string SourceLanguage = "*",
    string TargetLanguage = "*",
    string MatchMode = GlossaryMatchModes.Word,
    string Note = "");

public static class GlossaryMatchModes
{
    public const string Word = "word";
    public const string Contains = "contains";
    public const string CaseSensitive = "caseSensitive";
    public const string Regex = "regex";

    public static string Normalize(string? value) => value switch
    {
        Contains => Contains,
        CaseSensitive => CaseSensitive,
        Regex => Regex,
        _ => Word,
    };
}
