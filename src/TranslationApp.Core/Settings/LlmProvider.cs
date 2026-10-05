using System.Text.Json.Serialization;

namespace TranslationApp.Core.Settings;

/// <summary>
/// AI 供应商的请求协议（Wire API）。
/// <see cref="Chat"/> 走 OpenAI 兼容的 <c>/chat/completions</c>（messages 数组）；
/// <see cref="Responses"/> 走 OpenAI Responses API 的 <c>/responses</c>（instructions + input）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LlmWireApi
{
    /// <summary>OpenAI 兼容的对话补全（DeepSeek / Ollama / 通义千问兼容模式等）。</summary>
    Chat,

    /// <summary>OpenAI Responses API（GPT-5 系与新网关走这条）。</summary>
    Responses,
}

/// <summary>
/// 一档 AI 供应商配置（FR-022）：接口地址 + 密钥 + 模型 + 协议 + 鉴权开关 + 自定义请求头。
/// 从 0.3.x 的单档 Llm* 三字段演进而来（迁移见 <see cref="SettingsMigrations"/>）：
/// 家里 Ollama、公司网关、云上 API 可以同时存着，切换「当前档」即可，不必每次重填。
/// 密钥一律 DPAPI 密文（见 <see cref="SecretStore"/>），配置文件中没有明文。
/// </summary>
public sealed class LlmProvider
{
    /// <summary>首档固定 Id：迁移生成的第一档与出厂默认档都用它。</summary>
    public const string DefaultId = "default";

    /// <summary>档位标识（列表内唯一，稳定不变）。</summary>
    public string Id { get; set; } = "";

    /// <summary>供应商名称（界面上的档位名，可自由改）。</summary>
    public string Name { get; set; } = "";

    /// <summary>接口地址：填根地址、/v1 或完整 endpoint 均可，请求前按协议归一化。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API Key（DPAPI 密文）。</summary>
    public string ApiKeyEncrypted { get; set; } = "";

    /// <summary>模型名。</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 用户手填后「加入列表」的模型（自建候选），跨会话保留。
    /// 有些网关不提供 /models，只能自己攼一份常用的；下拉列表 = 自建 + 接口拉回（见 <see cref="MergeModels"/>）。
    /// </summary>
    public List<string> CustomModels { get; set; } = [];

    /// <summary>请求协议。</summary>
    public LlmWireApi WireApi { get; set; } = LlmWireApi.Chat;

    /// <summary>是否需要鉴权：关掉后不发 Authorization 头（本地 Ollama / vLLM / 内网网关）。</summary>
    public bool RequiresAuth { get; set; } = true;

    /// <summary>
    /// 额外请求头，JSON 对象（如 <c>{"X-Org":"team"}</c>）；空 = 不附加。
    /// 解析见 LlmTranslator.ParseExtraHeaders，格式不合法时按「没有自定义头」处理。
    /// </summary>
    public string ExtraHeadersJson { get; set; } = "";

    /// <summary>出厂默认档：DeepSeek（国内可直连，沿用 13.7 的既有默认值）。</summary>
    public static LlmProvider CreateDefault() => new()
    {
        Id = DefaultId,
        Name = "DeepSeek",
        BaseUrl = Translation.LlmTranslator.DefaultBaseUrl,
        Model = Translation.LlmTranslator.DefaultModel,
    };

    /// <summary>新建一档空白供应商（Id 唯一）。</summary>
    public static LlmProvider CreateBlank() => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
    };

    /// <summary>浅拷贝（迁移与界面新建时用，避免把设置里的实例直接交出去）。</summary>
    public LlmProvider Clone()
    {
        var clone = (LlmProvider)MemberwiseClone();
        // 列表是引用类型：浅拷贝会让两档共用同一个列表，必须单独复制
        clone.CustomModels = [.. CustomModels];
        return clone;
    }

    /// <summary>
    /// 合并候选模型：自建的排前面（常用的不用翻），接口拉回的排后面。
    /// 去空白、忽略大小写去重，各自保持原有顺序。
    /// </summary>
    public static IReadOnlyList<string> MergeModels(IEnumerable<string>? custom, IEnumerable<string>? fetched)
    {
        var merged = new List<string>();
        foreach (var source in new[] { custom, fetched })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var raw in source)
            {
                var name = (raw ?? "").Trim();
                if (name.Length == 0 || merged.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                merged.Add(name);
            }
        }

        return merged;
    }

    /// <summary>
    /// 拖动落点换算成自建列表下标（下拉里拖动排序用）。
    /// 合并列表 = 自建在前 + 接口拉回在后（见 <see cref="MergeModels"/>），
    /// <paramref name="dropSlot"/> 是落点在合并列表里的插入位置（0 = 最前，等于条数 = 最后），
    /// <paramref name="from"/> 是这条现在在自建列表里的下标，<paramref name="customCount"/> 是自建条数。
    /// 落到接口拉回的那一段上时，一律算「拖到自建列表末尾」。
    /// </summary>
    public static int IndexForDropSlot(int from, int dropSlot, int customCount)
    {
        if (customCount <= 0)
        {
            return 0;
        }

        // 自建条目就是合并列表的前 customCount 条：插入位置前面有几条自建 = min(插入位置, 自建条数)
        var index = Math.Min(dropSlot, customCount);
        if (dropSlot > from)
        {
            index--;   // 自己也被数进去了：先把自己摘出来，再插回去
        }

        return Math.Clamp(index, 0, customCount - 1);
    }

    /// <summary>
    /// 把自建列表里的 <paramref name="name"/> 挪到第 <paramref name="index"/> 位（下拉里拖动排序用）。
    /// 序号按「挪完之后」算：界面那边把插入线落在哪一格换算成下标再传进来。
    /// 名字不在自建列表里（比如拖的是接口拉回的那份），或本来就在这一位，就不动，返回 false。
    /// </summary>
    public bool MoveCustomModelToIndex(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var from = CustomModels.FindIndex(m => string.Equals(m, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (from < 0)
        {
            return false;
        }

        index = Math.Clamp(index, 0, CustomModels.Count - 1);
        if (index == from)
        {
            return false;
        }

        var moved = CustomModels[from];
        CustomModels.RemoveAt(from);
        CustomModels.Insert(index, moved);
        return true;
    }
}
