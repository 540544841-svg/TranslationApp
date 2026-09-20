namespace TranslationApp.Core.Translation;

/// <summary>
/// 术语表解析缓存（P0 批 1）：按 JSON 字符串比对失效，避免每次翻译都 parse。
/// AppSettings 为进程内单例且设置页原地改字段，读 <c>GlossaryJson</c> 即可感知变更。
/// 批 2（spec §4）加挂全局启用门控：<c>enabledProvider</c> 返回 false 时给空表（直通零替换），
/// 每读必查开关，不受 JSON 缓存影响。
/// </summary>
public sealed class GlossaryCache
{
    private readonly Func<string?> _jsonSource;
    private readonly Func<bool>? _enabledProvider;
    private readonly object _gate = new();
    private string? _lastJson;
    private IReadOnlyList<GlossaryItem> _items = Array.Empty<GlossaryItem>();

    public GlossaryCache(Func<string?> jsonSource, Func<bool>? enabledProvider = null)
    {
        _jsonSource = jsonSource;
        _enabledProvider = enabledProvider;
    }

    public IReadOnlyList<GlossaryItem> Items()
    {
        if (_enabledProvider?.Invoke() == false)
        {
            return Array.Empty<GlossaryItem>();
        }

        var json = _jsonSource();
        if (json == _lastJson) return _items;

        lock (_gate)
        {
            if (json != _lastJson)
            {
                _items = GlossaryReplacer.Parse(json, out _);
                _lastJson = json;
            }
        }
        return _items;
    }
}
