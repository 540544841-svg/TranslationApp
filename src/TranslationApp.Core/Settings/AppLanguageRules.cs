namespace TranslationApp.Core.Settings;

/// <summary>一条「按前台应用记忆语言对」规则（FR-058 / 批 6 spec §3）。</summary>
public sealed record AppLanguagePair(string Process, string SourceLanguage, string TargetLanguage);

/// <summary>
/// 按应用记语言对的纯逻辑（FR-058）：浏览器默认 en→zh、编辑器默认 zh→en 这类习惯，
/// 沙拉查词用户抱怨了很久——每次手动切语言是重复劳动。
///
/// 只认**进程名**（不认窗口标题：标题会变，而且更贴近"读取用户在看什么"）。
/// 进程名只在本机比较与存进 settings.json，绝不写日志、绝不出网。
/// </summary>
public static class AppLanguageRules
{
    /// <summary>规则条数上限（再多就成了"每条规则都想不起来改"的垃圾堆）。</summary>
    public const int MaxRules = 20;

    /// <summary>进程名最长保留长度（异常长的名字不是真进程名）。</summary>
    private const int MaxNameLength = 64;

    /// <summary>
    /// 归一化：<c>C:\Path\To\chrome.EXE</c> → <c>chrome</c>。
    /// 空、超长、含路径分隔符或控制字符 → 空串（表示"不可用"，调用方据此丢弃）。
    /// </summary>
    public static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "";
        }

        var name = processName.Trim();
        var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength)
        {
            return "";
        }

        return name.Any(char.IsControl) ? "" : name.ToLowerInvariant();
    }

    /// <summary>命中规则（先匹配者胜，规则顺序即优先级）；未命中返回 null。</summary>
    public static AppLanguagePair? Match(IReadOnlyList<AppLanguagePair> rules, string? processName)
    {
        var normalized = Normalize(processName);
        if (normalized.Length == 0)
        {
            return null;
        }

        return rules.FirstOrDefault(r => string.Equals(Normalize(r.Process), normalized, StringComparison.Ordinal));
    }

    /// <summary>
    /// 学习：把「当前应用 + 当前语言对」写进规则。同名覆盖并置顶（用户刚改的就是他想要的），
    /// 超出上限丢最旧的一条。进程名不可用时原样返回（不产生脏规则）。
    /// </summary>
    public static IReadOnlyList<AppLanguagePair> Learn(
        IReadOnlyList<AppLanguagePair> rules, string? processName, string sourceLanguage, string targetLanguage,
        int maxRules = MaxRules)
    {
        var normalized = Normalize(processName);
        if (normalized.Length == 0 || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return rules;
        }

        var next = new List<AppLanguagePair>(maxRules)
        {
            new(normalized, sourceLanguage, targetLanguage),
        };
        foreach (var rule in rules)
        {
            if (next.Count >= maxRules)
            {
                break;
            }

            if (!string.Equals(Normalize(rule.Process), normalized, StringComparison.Ordinal))
            {
                next.Add(rule);
            }
        }

        return next;
    }

    /// <summary>删掉某应用的规则（大小写/后缀不敏感）。</summary>
    public static IReadOnlyList<AppLanguagePair> Forget(IReadOnlyList<AppLanguagePair> rules, string? processName)
    {
        var normalized = Normalize(processName);
        return rules.Where(r => !string.Equals(Normalize(r.Process), normalized, StringComparison.Ordinal)).ToList();
    }
}
