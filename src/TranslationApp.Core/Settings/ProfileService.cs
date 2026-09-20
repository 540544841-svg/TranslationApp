using System.Text.Encodings.Web;
using System.Text.Json;

namespace TranslationApp.Core.Settings;

/// <summary>
/// 场景档案服务（FR-037 / spec §3）：档案 = 对固定 9 键的一份稀疏覆盖，切换即写回 <see cref="AppSettings"/>
/// （设置项仍是唯一事实源，档案只是「宏」，不加二级缓存）。落盘由调用方（App 层）负责。
/// 内置「阅读 / 隐私 / 写作」三档为代码常量：不落盘、不可改删。
/// </summary>
public sealed class ProfileService(AppSettings settings)
{
    /// <summary>隐式档案名：ActiveProfile 为空时的显示值，切换 = 只清标记、不动任何键。</summary>
    public const string StandardName = "标准";

    public const int MaxCustomProfiles = 10;
    public const int MaxProfileNameLength = 20;

    /// <summary>「阅读」档：复制即读的顺手链路（spec §3.2；剪贴板监听不强制代开）。</summary>
    public static readonly AppProfile Reading = new()
    {
        Name = "阅读",
        Overrides = new ProfileOverrides
        {
            Engine = "bing",
            SourceLanguage = "auto",
            TargetLanguage = "zh-CN",
            CleanClipboardText = true,
            PrivacyMode = false,
            GlossaryEnabled = true,
        },
    };

    /// <summary>「隐私」档：本地留痕全关 + 不装钩子（翻译请求本身仍会发送，边界与 FR-032 一致）。</summary>
    public static readonly AppProfile Privacy = new()
    {
        Name = "隐私",
        Overrides = new ProfileOverrides
        {
            PrivacyMode = true,
            ClipboardMonitorEnabled = false,
            HoverSelectEnabled = false,
            GlossaryEnabled = true,
        },
    };

    /// <summary>
    /// 「写作」档（FR-054 / B3-1 追加）：AI 引擎 + 正式风格 + 关阅读清洗（自己写的内容不该被合并换行）。
    /// **刻意不钉目标语言**——"写作"对俄语/英语用户含义不同，钉住会让切换档案顺手改语言，
    /// 违反档案的稀疏语义（未指定的键不动）。
    /// </summary>
    public static readonly AppProfile Writing = new()
    {
        Name = "写作",
        Overrides = new ProfileOverrides
        {
            Engine = "llm",
            Style = "formal",
            CleanClipboardText = false,
            GlossaryEnabled = true,
        },
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>当前档案显示名（空 ActiveProfile 归一为「标准」）。</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(settings.ActiveProfile) ? StandardName : settings.ActiveProfile;

    /// <summary>自定义档案（JSON 损坏按空表处理，下次保存自然覆盖修复）。</summary>
    public IReadOnlyList<AppProfile> CustomProfiles()
    {
        try
        {
            return JsonSerializer.Deserialize<List<AppProfile>>(settings.CustomProfilesJson, JsonOptions)?
                       .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                       .ToList()
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>全部可选档案：内置三档在前，自定义按保存顺序。</summary>
    public IReadOnlyList<AppProfile> AllProfiles() => [Reading, Privacy, Writing, .. CustomProfiles()];

    /// <summary>
    /// 应用档案：「标准」与未知名都只清 ActiveProfile、不动任何键（档案被删除后配置不莫名变化）。
    /// 副作用（剪贴板监听 / 鼠标钩子 / 隐私门控）由 App 层在 Save 之后统一走 ApplyPrivacySideEffects。
    /// </summary>
    public void Apply(string name)
    {
        var profile = string.IsNullOrWhiteSpace(name) || name == StandardName
            ? null
            : AllProfiles().FirstOrDefault(p => p.Name == name);

        profile?.Overrides.ApplyTo(settings);
        settings.ActiveProfile = profile?.Name ?? "";
    }

    /// <summary>循环顺序：标准 → 阅读 → 隐私 → 写作 → 自定义 1..n → 标准（当前档案已被删除时回到标准）。</summary>
    public string NextProfileName()
    {
        var names = new List<string> { StandardName };
        names.AddRange(AllProfiles().Select(p => p.Name));
        var index = names.IndexOf(DisplayName);
        return index < 0 ? StandardName : names[(index + 1) % names.Count];
    }

    /// <summary>档案生效中、但任一被覆盖键的现值已偏离档案值（用户手改过设置）。</summary>
    public bool IsDeviation()
    {
        var profile = AllProfiles().FirstOrDefault(p => p.Name == settings.ActiveProfile);
        return profile is not null && profile.Overrides.DeviatesFrom(settings);
    }

    /// <summary>把当前 8 键快照存为自定义档案。名称为空/超长/与任何档案重名/超上限 → false。</summary>
    public bool SaveCurrentAs(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxProfileNameLength)
        {
            return false;
        }

        if (AllProfiles().Any(p => p.Name == name))
        {
            return false; // 与内置或自定义重名
        }

        var custom = CustomProfiles().ToList();
        if (custom.Count >= MaxCustomProfiles)
        {
            return false;
        }

        custom.Add(new AppProfile { Name = name, Overrides = ProfileOverrides.FromSettings(settings) });
        settings.CustomProfilesJson = JsonSerializer.Serialize(custom, JsonOptions);
        return true;
    }

    /// <summary>删除自定义档案；正在使用该档案时同时退回标准。不存在 → false。</summary>
    public bool DeleteCustom(string name)
    {
        var custom = CustomProfiles().ToList();
        var target = custom.FirstOrDefault(p => p.Name == name);
        if (target is null)
        {
            return false;
        }

        custom.Remove(target);
        settings.CustomProfilesJson = JsonSerializer.Serialize(custom, JsonOptions);
        if (settings.ActiveProfile == name)
        {
            settings.ActiveProfile = "";
        }

        return true;
    }
}
