using System.Net;
using System.Net.Http;
using System.Text;
using TranslationApp.Core.Settings;

namespace TranslationApp.Core.Translation;

/// <summary>
/// 官方引擎（腾讯 TMT / 百度 / Azure / DeepL / AI）的抽象基类（13.1.1）。
/// 收敛四家共性，避免四份复制：分块、超时预算、网络类错误重试 1 次、错误分类、Key 读取。
/// 各引擎只负责自己的语言码映射、请求构造与响应解析。
/// </summary>
public abstract class OfficialTranslatorBase : ITranslator
{
    /// <summary>短文本请求预算（FR-006）。</summary>
    private static readonly TimeSpan BaseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>长文本额外预算：一个整块约 12s（服务端翻译耗时随文本增长）。</summary>
    private static readonly TimeSpan TimeoutPerFullChunk = TimeSpan.FromSeconds(12);

    /// <summary>预算上限（FR-006：长文本最多 20s）。</summary>
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClientProvider _httpProvider;

    protected OfficialTranslatorBase(AppSettings settings, HttpClientProvider httpProvider)
    {
        Settings = settings;
        _httpProvider = httpProvider;
    }

    /// <summary>当前设置（只读用途：取 Key、Region 等）。</summary>
    protected AppSettings Settings { get; }

    /// <summary>本引擎单次请求的文本长度上限（各家不同，见 13.1）。</summary>
    protected abstract int MaxChunkLength { get; }

    /// <summary>
    /// 本引擎请求所用的代理作用范围（13.1.1）：腾讯/百度走「全部引擎」；
    /// Azure/DeepL/AI 属国外引擎，走「仅国外引擎」。
    /// 声明为 protected internal 是为了让单元测试能断言各引擎的归属，防止后续新增引擎时配错。
    /// </summary>
    protected internal abstract ProxyScope Proxy { get; }

    /// <summary>本引擎的 HttpClient（按代理范围从共享 Provider 取，代理设置变更即时生效）。</summary>
    protected HttpClient Http => _httpProvider.Get(Proxy);

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract bool IsConfigured { get; }

    /// <summary>内部语言码（= Google 码）→ 本引擎语言码；返回 null 表示不支持该语言。</summary>
    protected abstract string? ToEngineLanguage(string internalCode);

    /// <summary>
    /// 翻译单个分块。返回的检测语言必须是内部语言码（调用方负责映射）。
    /// 网络层异常请用 <see cref="NetworkError"/> / <see cref="TimeoutError"/> 包装后抛出。
    /// </summary>
    protected abstract Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, CancellationToken cancellationToken);

    /// <summary>
    /// 带调用方指令（语境 / 风格）的分块请求（FR-050 / FR-051）。
    /// 默认**忽略指令**转调无指令版——只有 AI 引擎覆写它，其余官方引擎零改动、语义不变。
    /// </summary>
    protected virtual Task<(string Text, string? DetectedSourceLanguage)> RequestChunkAsync(
        string text, string engineSource, string engineTarget, TranslationDirective directive, CancellationToken cancellationToken) =>
        RequestChunkAsync(text, engineSource, engineTarget, cancellationToken);

    public Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(text, sourceLanguage, targetLanguage, TranslationDirective.None, cancellationToken);

    /// <summary>
    /// 分块翻译主流程。<paramref name="directive"/> 逐块透传：风格要求作用于每个分块，
    /// 语境是「上一段」而非「前一块」，故同一请求内各块共用不变。
    /// </summary>
    protected async Task<TranslationResult> TranslateCoreAsync(
        string text, string sourceLanguage, string targetLanguage, TranslationDirective directive, CancellationToken cancellationToken)
    {
        // 语言码缺失时明确报错，不静默透传（13.1.7：映射缺失时给出「不支持此语言」提示）
        var target = ToEngineLanguage(targetLanguage)
            ?? throw new TranslationException(TranslationErrorType.Engine, UnsupportedLanguageMessage("目标", targetLanguage));
        var source = ToEngineLanguage(sourceLanguage)
            ?? throw new TranslationException(TranslationErrorType.Engine, UnsupportedLanguageMessage("源", sourceLanguage));

        var builder = new StringBuilder();
        string? detected = null;

        foreach (var chunk in TextChunker.Split(text, MaxChunkLength))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (translated, detectedInChunk) = await TranslateChunkWithRetryAsync(
                chunk, source, target, directive, cancellationToken);
            builder.Append(translated);
            detected ??= detectedInChunk;
        }

        return new TranslationResult(builder.ToString(), detected);
    }

    /// <summary>仅网络类错误重试 1 次（FR-006）；其余错误直接抛出，由调用方提示用户。</summary>
    private async Task<(string Text, string? Detected)> TranslateChunkWithRetryAsync(
        string chunk, string engineSource, string engineTarget, TranslationDirective directive, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RequestChunkAsync(chunk, engineSource, engineTarget, directive, cancellationToken);
            }
            catch (TranslationException ex)
                when (ex.ErrorType == TranslationErrorType.Network && attempt == 1)
            {
                // 网络抖动重试一次
            }
        }
    }

    /// <summary>
    /// 创建本次请求的超时令牌：按文本长度给预算，同时挂上调用方的取消令牌，
    /// 使「关窗 / 退出对比」能立即中止在途请求。
    /// </summary>
    private CancellationTokenSource CreateRequestTimeout(int textLength, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeoutFor(textLength));
        return cts;
    }

    /// <summary>
    /// 发送请求并读回响应体：统一挂超时预算与调用方取消令牌，把网络异常归类为 Network。
    /// 不在内部按状态码抛异常，因为同一状态码在不同引擎的归类不同（如 402 在 LLM 属额度、在
    /// Azure 属凭证），判定交给各引擎；<see cref="FromHttpStatus"/> 提供通用兜底。
    /// </summary>
    protected async Task<(bool IsSuccess, HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request, int textLength, CancellationToken cancellationToken)
    {
        using var timeout = CreateRequestTimeout(textLength, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw NetworkError(ex, "网络不可达");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimeoutError(ex);
        }

        using (response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                return (response.IsSuccessStatusCode, response.StatusCode, body);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // 预算在读取响应体期间到期，同样算请求超时（否则会漏成未分类异常）
                throw TimeoutError(ex);
            }
        }
    }

    /// <summary>
    /// 按文本长度决定本次请求预算：短文本 8s，长文本按比例放宽，上限 20s（FR-006 模型）。
    /// 声明为 virtual：AI（LLM）通道的超时预算不同（短 20s / 长 60s，见 13.3），届时覆写即可。
    /// </summary>
    protected internal virtual TimeSpan TimeoutFor(int textLength)
    {
        var ratio = Math.Min(1.0, (double)textLength / MaxChunkLength);
        var budget = BaseTimeout + TimeoutPerFullChunk * ratio;
        return budget > MaxTimeout ? MaxTimeout : budget;
    }

    /// <summary>网络不可达（DNS/连接失败等）。</summary>
    protected internal static TranslationException NetworkError(Exception inner, string message) =>
        new(TranslationErrorType.Network, message, inner);

    /// <summary>请求超时（取消令牌到期）。调用方主动取消时应改用 <see cref="OperationCanceledException"/>。</summary>
    protected internal static TranslationException TimeoutError(Exception inner) =>
        new(TranslationErrorType.Network, "请求超时", inner);

    /// <summary>
    /// HTTP 状态码兜底映射：各引擎自己的错误码优先，这里只处理「响应体没给出错误码」或
    /// 标准 HTTP 语义明确的情况（13.1.1：HTTP 状态 + 引擎错误码共同决定分类）。
    /// 402 Payment Required 表示欠费/额度问题，故归入 QuotaExceeded。
    /// </summary>
    protected internal static TranslationException FromHttpStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new TranslationException(TranslationErrorType.InvalidKey, "密钥无效或无权访问"),
        HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests =>
            new TranslationException(TranslationErrorType.QuotaExceeded, "额度用尽或触发限流"),
        _ => new TranslationException(TranslationErrorType.Engine, $"引擎返回 HTTP {(int)status}"),
    };

    /// <summary>必填标识 + 密文密钥都齐全且密钥可解密，才算「已配置」（13.1.1）。</summary>
    protected static bool HasSecret(string? plainIdentifier, string? encryptedSecret) =>
        !string.IsNullOrWhiteSpace(plainIdentifier) && !string.IsNullOrWhiteSpace(ReadSecret(encryptedSecret));

    /// <summary>取出明文密钥（仅在内存中使用，绝不落盘或写入日志）。</summary>
    protected static string ReadSecret(string? encryptedSecret) =>
        SecretStore.Unprotect(encryptedSecret) ?? "";

    private string UnsupportedLanguageMessage(string kind, string code) =>
        $"{Name} 不支持该{kind}语言（{code}），请在设置中更换语言或引擎";
}
