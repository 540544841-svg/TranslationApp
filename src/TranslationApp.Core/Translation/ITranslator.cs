namespace TranslationApp.Core.Translation;

/// <summary>翻译结果：译文 + 检测到的源语言（自动检测时回填用）。</summary>
public sealed record TranslationResult(string TranslatedText, string? DetectedSourceLanguage);

/// <summary>翻译错误分类（FR-006：不同错误给不同提示文案）。</summary>
public enum TranslationErrorType
{
    /// <summary>网络不可达 / 请求超时。</summary>
    Network,

    /// <summary>引擎接口异常（非 200、响应格式异常等）。</summary>
    Engine,

    /// <summary>API Key 无效或未配置（阶段 2 官方引擎）。</summary>
    InvalidKey,

    /// <summary>配额用尽 / 触发限流（阶段 2 官方引擎）。</summary>
    QuotaExceeded,
}

/// <summary>翻译引擎异常，携带错误分类。</summary>
public sealed class TranslationException : Exception
{
    public TranslationErrorType ErrorType { get; }

    public TranslationException(TranslationErrorType errorType, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorType = errorType;
    }
}

/// <summary>
/// 翻译引擎抽象（FR-006）：各引擎独立实现类，DI 注册，设置页可切换。
/// </summary>
public interface ITranslator
{
    /// <summary>引擎标识（配置文件中持久化，勿随意变更）。</summary>
    string Id { get; }

    /// <summary>引擎显示名。</summary>
    string Name { get; }

    /// <summary>是否已配置（官方引擎未配置 Key 时为 false，设置页显示「未配置」）。</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// 翻译文本。sourceLanguage 传 "auto" 表示自动检测。
    /// 网络类失败内部重试 1 次后仍失败，抛 <see cref="TranslationException"/>。
    /// </summary>
    /// <param name="cancellationToken">
    /// 取消在途请求（关窗 / 退出对比即取消，13.1.1）；取消时抛 <see cref="OperationCanceledException"/>，不算翻译失败。
    /// </param>
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
