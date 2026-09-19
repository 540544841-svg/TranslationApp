namespace TranslationApp.Core.Translation;

/// <summary>
/// 术语表解析缓存（P0 批 1）：按 JSON 字符串比对失效，避免每次翻译都 parse。
/// AppSettings 为进程内单例且设置页原地改字段，读 <c>GlossaryJson</c> 即可感知变更。
/// </summary>
public sealed class GlossaryCache
{
    private readonly Func<string?> _jsonSource;
    private readonly object _gate = new();
    private string? _lastJson;
    private IReadOnlyList<GlossaryItem> _items = Array.Empty<GlossaryItem>();

    public GlossaryCache(Func<string?> jsonSource) => _jsonSource = jsonSource;

    public IReadOnlyList<GlossaryItem> Items()
    {
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
