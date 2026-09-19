using TranslationApp.Core.History;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 引擎装饰器（P0 批 1 / spec §1.3 §4.2）：在 <see cref="TranslatorCatalog"/> 装配处统一包住每个引擎，
/// 使小窗 / 钉图 / 对比 / 测试连接全部调用点零改动获得——
/// ① 译文术语表后置替换（命中数与明细回填到 <see cref="TranslationResult"/>）；
/// ② 成败统计（成功 +1；<see cref="TranslationException"/> 按 ErrorType 计入对应失败列；
///   取消不计；隐私模式或 <see cref="SuppressStats"/> 作用域内整体不记——「测试连接」用它排除计数）。
/// 装饰器本身绝不抛出新异常、绝不吞掉引擎异常。
/// </summary>
public sealed class GlossaryTranslator : ITranslator
{
    private static readonly AsyncLocal<int> SuppressDepth = new();

    /// <summary>抑制当前异步流内的统计记录（测试连接等旁路调用用），术语替换不受影响。</summary>
    public static IDisposable SuppressStats() => new SuppressScope();

    private sealed class SuppressScope : IDisposable
    {
        private bool _done;
        internal SuppressScope() => SuppressDepth.Value++;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            SuppressDepth.Value--;
        }
    }

    private readonly ITranslator _inner;
    private readonly Func<IReadOnlyList<GlossaryItem>> _glossary;
    private readonly Action<string, EngineOutcome, string?>? _recordOutcome;
    private readonly Func<bool> _privacyMode;

    public GlossaryTranslator(
        ITranslator inner,
        Func<IReadOnlyList<GlossaryItem>> glossary,
        Action<string, EngineOutcome, string?>? recordOutcome = null,
        Func<bool>? privacyMode = null)
    {
        _inner = inner;
        _glossary = glossary;
        _recordOutcome = recordOutcome;
        _privacyMode = privacyMode ?? (() => false);
    }

    public string Id => _inner.Id;
    public string Name => _inner.Name;
    public bool IsConfigured => _inner.IsConfigured;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        TranslationResult result;
        try
        {
            result = await _inner.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不算失败（13.1.1），不计数
        }
        catch (TranslationException ex)
        {
            Record(MapOutcome(ex.ErrorType), ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            Record(EngineOutcome.FailEngine, ex.Message);
            throw;
        }

        var applied = ApplyGlossary(result);
        Record(EngineOutcome.Success, null);
        return applied;
    }

    private TranslationResult ApplyGlossary(TranslationResult result)
    {
        try
        {
            var items = _glossary();
            if (items.Count == 0) return result;

            var g = GlossaryReplacer.Apply(result.TranslatedText, items);
            return g.Hits > 0
                ? result with { TranslatedText = g.Text, GlossaryHits = g.Hits, GlossaryApplied = g.Applied }
                : result;
        }
        catch
        {
            return result; // 术语层任何异常都不能弄坏翻译结果
        }
    }

    private void Record(EngineOutcome outcome, string? error)
    {
        if (_recordOutcome is null || SuppressDepth.Value > 0) return;
        try
        {
            if (_privacyMode()) return;
            _recordOutcome(_inner.Id, outcome, error);
        }
        catch
        {
            // 统计失败不影响翻译
        }
    }

    private static EngineOutcome MapOutcome(TranslationErrorType errorType) => errorType switch
    {
        TranslationErrorType.Network => EngineOutcome.FailNetwork,
        TranslationErrorType.InvalidKey => EngineOutcome.FailKey,
        TranslationErrorType.QuotaExceeded => EngineOutcome.FailQuota,
        _ => EngineOutcome.FailEngine,
    };
}
