namespace TranslationApp.Core.Capture;

/// <summary>
/// 本地 OCR 引擎抽象（FR-030 / 14.9.2「双引擎策略」）：BGRA32 缓冲 → 识别文字 + 行/词框布局。
/// 两个实现都在 App 层：<c>WindowsOcrEngine</c>（Windows.Media.Ocr，auto 双跑择优与几何覆盖率门控在其内部）
/// 与 <c>PaddleOcrEngine</c>（RapidOcrNet 适配器）。Core 只定义契约与纯逻辑，不新增任何 NuGet 依赖。
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// 识别 BGRA32 缓冲并取回文字位置信息。坐标属于传入的那张位图（识别前缩小过则由调用方按
    /// <see cref="OcrLayoutRules.Create"/> 还原）。返回 null 表示引擎不可用（输入非法/引擎创建失败）；
    /// 线条可能为空（画面里没有字）。
    /// </summary>
    /// <param name="bgra">BGRA32 位图缓冲（4 字节/像素，行序自上而下）。</param>
    /// <param name="width">位图宽（像素）。</param>
    /// <param name="height">位图高（像素）。</param>
    /// <param name="languageTag">识别语言标签（auto 或 BCP-47）；paddle 引擎忽略（rec 为中英日混训单模型）。</param>
    Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag);
}

/// <summary>一次带布局的识别结果（<see cref="OcrLineBox"/> 的坐标属于传入的那张位图）。</summary>
/// <param name="Text">按行 \n 连接的识别文本（与 <c>OcrResult.Text</c> 同语义）。</param>
/// <param name="Lines">视觉行（行框 = 该行词框并集）。</param>
/// <param name="TextAngle">引擎报告的顺时针角度；**实测正常横排也不是 null 而是 −0**。</param>
/// <param name="EngineTag">**实际使用的识别器语言标签**（C-⑥：auto 双跑选中哪个引擎就是哪个，
/// 显式指定时为该引擎回传的真实标签）；供调用方映射翻译源语言。paddle 路径恒为 null
/// （中英日混训 rec 无单一识别语言，交回翻译引擎自动检测，<see cref="OcrLanguages.ReconcileWithScript"/> 照常兜底）。</param>
public sealed record OcrRecognition(
    string Text, IReadOnlyList<OcrLineBox> Lines, double? TextAngle, string? EngineTag = null);
