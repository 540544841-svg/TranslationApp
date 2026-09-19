namespace TranslationApp.Core.Translation;

/// <summary>
/// 引擎目录（FR-006）：持有全部已注册引擎，按设置中的引擎 Id 解析当前引擎。
/// 引擎为单例、无状态，切换即时生效，无需重启。
/// </summary>
public sealed class TranslatorCatalog
{
    private readonly IReadOnlyList<ITranslator> _engines;
    private readonly ITranslator _fallback;

    public TranslatorCatalog(IEnumerable<ITranslator> engines)
    {
        _engines = engines.ToArray();
        if (_engines.Count == 0)
        {
            throw new ArgumentException("至少需要注册一个翻译引擎", nameof(engines));
        }
        _fallback = _engines[0];
    }

    /// <summary>全部引擎（设置页下拉展示顺序）。</summary>
    public IReadOnlyList<ITranslator> All => _engines;

    /// <summary>按引擎 Id 解析；未找到或该引擎未配置时回退到首个可用引擎，保证始终可翻译。</summary>
    public ITranslator Resolve(string? engineId)
    {
        var engine = _engines.FirstOrDefault(e => e.Id == engineId && e.IsConfigured);
        return engine ?? _engines.FirstOrDefault(e => e.IsConfigured) ?? _fallback;
    }

    /// <summary>
    /// 按 Id **精确**查找引擎（不回落）。
    /// FR-028 判定备用引擎时必须用它而不是 <see cref="Resolve"/>：
    /// Resolve 会回落到首个可用引擎，那会把「备用引擎未配置 / 不存在」静默换成另一个引擎，
    /// 等于在用户不知情时动用其官方引擎配额（14.4.1 明确禁止）。
    /// </summary>
    public ITranslator? Find(string? engineId) =>
        string.IsNullOrWhiteSpace(engineId)
            ? null
            : _engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.Ordinal));
}
