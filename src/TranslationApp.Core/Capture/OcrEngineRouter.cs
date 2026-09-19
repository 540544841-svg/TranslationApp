namespace TranslationApp.Core.Capture;

/// <summary>
/// 本地 OCR 引擎路由（FR-030 / 14.9.2「双引擎策略」与「降级策略」）：
/// 按设置在 windows / paddle 间分发；paddle 初始化失败或识别异常/超时（初始化 ≤10s、单次识别 ≤5s，
/// 超时按失败计）时**本进程内永久**回退 windows 引擎并触发一次性用户提示——故障隔离：
/// 不得崩溃、不得无提示失败、不反复重试初始化（14.9.2 降级策略①②）。
/// 默认路径（windows）只有一次字符串比较的分发开销，识别行为与 FR-030 之前逐字节一致。
/// </summary>
public sealed class OcrEngineRouter
{
    /// <summary>paddle 不可用时的一次性提示文案（14.9.2 降级策略①，App 层接托盘气泡）。</summary>
    public const string PaddleFallbackNotice = "PaddleOCR 引擎不可用，已改用系统识别";

    private readonly IOcrEngine _windows;
    private readonly IOcrEngine? _paddle;
    private readonly Func<string?> _localEngine;
    private readonly Action<string>? _notifyFallback;
    private readonly Action<Exception>? _logPaddleFailure;
    private bool _paddleDegraded;

    /// <summary>
    /// </summary>
    /// <param name="windows">windows 引擎（默认路径与降级落点；进程生命周期内必可用）。</param>
    /// <param name="paddle">paddle 引擎；null = 运行库/模型未随包分发，恒走 windows。</param>
    /// <param name="localEngine">设置读取器（<c>OcrLocalEngine</c> 原始值，每次识别时读取以即时生效）。</param>
    /// <param name="notifyFallback">一次性用户提示回调（托盘气泡）；null = 仅降级不提示。</param>
    /// <param name="logPaddleFailure">paddle 失败的 Warning 日志回调（Core 不依赖日志框架）。</param>
    public OcrEngineRouter(
        IOcrEngine windows,
        IOcrEngine? paddle,
        Func<string?> localEngine,
        Action<string>? notifyFallback = null,
        Action<Exception>? logPaddleFailure = null)
    {
        _windows = windows;
        _paddle = paddle;
        _localEngine = localEngine;
        _notifyFallback = notifyFallback;
        _logPaddleFailure = logPaddleFailure;
    }

    /// <summary>paddle 是否已在本进程内降级（诊断与测试用；降级后所有识别恒走 windows）。</summary>
    public bool IsPaddleDegraded => _paddleDegraded;

    /// <summary>按设置分发识别（<see cref="IOcrEngine.RecognizeAsync"/> 同签名，调用方无感）。</summary>
    public async Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
    {
        if (_paddle is not null
            && !_paddleDegraded
            && string.Equals(OcrEngineNames.Normalize(_localEngine()), OcrEngineNames.Paddle, StringComparison.Ordinal))
        {
            try
            {
                return await _paddle.RecognizeAsync(bgra, width, height, languageTag);
            }
            catch (Exception ex)
            {
                // 14.9.2 降级策略①②同一处理：标记降级（本次及后续自动回退）+ Warning 日志 + 一次性提示
                _paddleDegraded = true;
                _logPaddleFailure?.Invoke(ex);
                _notifyFallback?.Invoke(PaddleFallbackNotice);
            }
        }

        return await _windows.RecognizeAsync(bgra, width, height, languageTag);
    }
}
