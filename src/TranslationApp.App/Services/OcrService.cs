using Serilog;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Settings;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace TranslationApp.Services;

/// <summary>
/// 本地 OCR 门户（FR-030 / 14.9.2「双引擎策略」）：保留 FR-021 以来的全部对外成员
/// （能力探测、语言解析、识别），识别按设置 <c>OcrLocalEngine</c> 经 <see cref="OcrEngineRouter"/>
/// 分发到 <see cref="WindowsOcrEngine"/>（默认，Windows.Media.Ocr，auto 双跑择优与几何覆盖率门控在其内部）
/// 或 <see cref="PaddleOcrEngine"/>（RapidOcrNet 适配器，懒加载 + 空闲释放）；paddle 初始化失败或
/// 识别异常/超时自动回退 windows 引擎并一次性提示（14.9.2 降级策略）。
/// 默认路径（windows）行为与 FR-030 之前逐字节一致（AC 1 零回归）。
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngineRouter _router;
    private readonly IReadOnlyList<OcrLanguageTag> _availableLanguages;

    /// <summary>
    /// </summary>
    /// <param name="settings">应用设置（每次识别时读 <c>OcrLocalEngine</c>/<c>OcrPaddleResident</c>，设置即时生效）。</param>
    /// <param name="notifyFallback">paddle 降级时的一次性气泡回调（标题, 文案）；null = 仅降级不提示。</param>
    public OcrService(AppSettings settings, Action<string, string>? notifyFallback = null)
    {
        try
        {
            MaxImageDimension = (int)OcrEngine.MaxImageDimension;
            _availableLanguages = OcrEngine.AvailableRecognizerLanguages
                .Select(language => new OcrLanguageTag(language.LanguageTag, language.DisplayName))
                .ToArray();
        }
        catch (Exception ex)
        {
            // 极端环境（如 WinRT 组件被裁剪）下不得让启动失败，降级为「不可用」
            Log.Error(ex, "探测系统 OCR 能力失败，截图翻译降级为不可用");
            MaxImageDimension = 0;
            _availableLanguages = [];
        }

        Log.Information(
            "OCR 能力探测：语言包 {Count} 个（{Languages}），MaxImageDimension={Dimension}",
            _availableLanguages.Count,
            _availableLanguages.Count == 0 ? "无" : string.Join("、", _availableLanguages.Select(l => l.Tag)),
            MaxImageDimension);

        _router = new OcrEngineRouter(
            new WindowsOcrEngine(_availableLanguages),
            new PaddleOcrEngine(() => settings.OcrPaddleResident),
            () => settings.OcrLocalEngine,
            message => notifyFallback?.Invoke("速译 - 截图翻译", message),
            ex => Log.Warning(ex, "PaddleOCR 引擎失败，本进程内降级为系统识别引擎（OcrLocalEngine 设置保留不变）"));
    }

    /// <summary>系统是否安装了至少一种 OCR 语言包。</summary>
    public bool IsAvailable => _availableLanguages.Count > 0;

    /// <summary>系统可用的 OCR 识别语言。</summary>
    public IReadOnlyList<OcrLanguageTag> AvailableLanguages => _availableLanguages;

    /// <summary>OcrEngine.MaxImageDimension 的运行期实际取值（本机实测 10000）。</summary>
    public int MaxImageDimension { get; }

    /// <summary>按设置值解析实际使用的 OCR 语言与翻译源语言（13.2.5）。</summary>
    public OcrLanguageStatus ResolveStatus(string? setting) => OcrLanguages.Resolve(setting, _availableLanguages);

    /// <summary>
    /// 识别 BGRA32 缓冲并**同时取回文字位置信息**（FR-027 / 14.3.1）：词框 → 行框并集 + <c>TextAngle</c>。
    /// 坐标属于传入的这张位图（实测确认），若识别前缩小过则由调用方按 `1/Ratio` 还原
    /// （用 <see cref="OcrLayoutRules.Create"/>）。
    /// 返回 null 表示引擎不可用（语言包缺失/输入非法/paddle 失败降级后仍失败）；线条可能为空。
    /// <para>引擎选择由 <see cref="OcrEngineRouter"/> 按设置分发；windows 引擎的 auto 双跑在其内部。</para>
    /// </summary>
    public Task<OcrRecognition?> RecognizeWithLayoutAsync(byte[] bgra, int width, int height, string? languageTag) =>
        _router.RecognizeAsync(bgra, width, height, languageTag);

    /// <summary>识别 BGRA32 缓冲（旧链路文本口）。返回 null 表示引擎不可用；文本可能为空串（画面里没有字）。</summary>
    public async Task<string?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag) =>
        (await RecognizeWithLayoutAsync(bgra, width, height, languageTag))?.Text;
}
