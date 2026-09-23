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
public sealed class GlossaryTranslator : ITranslator, IPromptDirectiveTranslator
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
    private readonly Action<string, EngineOutcome, string?, long?>? _recordOutcome;
    private readonly Func<bool> _privacyMode;

    public GlossaryTranslator(
        ITranslator inner,
        Func<IReadOnlyList<GlossaryItem>> glossary,
        Action<string, EngineOutcome, string?, long?>? recordOutcome = null,
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

    /// <summary>被包装的真实引擎（预热等需要访问引擎特有成员的路径用，如 BingTranslator.WarmUpAsync）。</summary>
    public ITranslator Inner => _inner;

    /// <summary>剥掉装饰层取回真实引擎；未包装时原样返回。</summary>
    public static ITranslator Unwrap(ITranslator translator) =>
        translator is GlossaryTranslator g ? Unwrap(g.Inner) : translator;

    public Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (_inner is IPromptDirectiveTranslator directable)
        {
            return RunAsync(
                () => directable.TranslateAsync(text, sourceLanguage, targetLanguage, TranslationDirective.None, cancellationToken),
                text, sourceLanguage, targetLanguage, cancellationToken);
        }

        return RunAsync(
            () => _inner.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken),
            text, sourceLanguage, targetLanguage, cancellationToken);
    }

    /// <summary>
    /// 带指令的翻译（FR-050 / FR-051）：inner 支持就转发（指令只影响 Prompt，术语替换仍只作用于原文与译文），
    /// inner 不支持则**忽略指令**照常翻译并记统计——由 UI 保证不会给不支持的引擎派指令（按钮不显示）。
    /// </summary>
    public Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        TranslationDirective directive,
        CancellationToken cancellationToken = default)
    {
        if (_inner is not IPromptDirectiveTranslator directable)
        {
            return TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        }

        return RunAsync(
            () => directable.TranslateAsync(text, sourceLanguage, targetLanguage, directive, cancellationToken),
            text, sourceLanguage, targetLanguage, cancellationToken);
    }

    /// <summary>请求 + 术语后置替换 + 成败统计的公共尾巴（两种入口共用，行为完全一致）。</summary>
    private async Task<TranslationResult> RunAsync(
        Func<Task<TranslationResult>> request, string sourceText,
        string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        TranslationResult result;
        var startedAt = Environment.TickCount64;
        try
        {
            result = await request();
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw; // 引擎内部的超时取消同样抛 OperationCanceledException：由令牌判定是否算失败
        }
        catch (TranslationException ex)
        {
            Record(MapOutcome(ex.ErrorType), ex.Message, null);
            throw;
        }
        catch (Exception ex)
        {
            Record(EngineOutcome.FailEngine, ex.Message, null);
            throw;
        }

        var elapsed = Environment.TickCount64 - startedAt;
        var applied = ApplyGlossary(result, sourceText, sourceLanguage, targetLanguage);
        Record(EngineOutcome.Success, null, elapsed);
        return applied;
    }

    /// <summary>术语替换（传入原文以启用 FR-041 反向保护：Target 已在原文中的词条跳过替换）。</summary>
    private TranslationResult ApplyGlossary(
        TranslationResult result, string sourceText, string sourceLanguage, string targetLanguage)
    {
        try
        {
            var items = _glossary();
            if (items.Count == 0) return result;

            var g = GlossaryReplacer.Apply(
                result.TranslatedText, items, sourceText, sourceLanguage, targetLanguage);
            if (g.Hits == 0 && g.Conflicts.Count == 0) return result;
            return result with
            {
                TranslatedText = g.Text,
                GlossaryHits = g.Hits,
                GlossaryApplied = g.Applied,
                GlossaryConflicts = g.Conflicts,
            };
        }
        catch
        {
            return result; // 术语层任何异常都不能弄坏翻译结果
        }
    }

    private void Record(EngineOutcome outcome, string? error, long? latencyMs)
    {
        if (_recordOutcome is null || SuppressDepth.Value > 0) return;
        try
        {
            if (_privacyMode()) return;
            _recordOutcome(_inner.Id, outcome, error, latencyMs);
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
