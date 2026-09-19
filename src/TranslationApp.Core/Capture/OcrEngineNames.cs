namespace TranslationApp.Core.Capture;

/// <summary>本地 OCR 识别引擎取值（FR-030 / 14.9.3 设置字段 <c>OcrLocalEngine</c>）。</summary>
public static class OcrEngineNames
{
    /// <summary>Windows.Media.Ocr（默认）：零依赖、快，行为与 FR-030 之前完全一致。</summary>
    public const string Windows = "windows";

    /// <summary>PaddleOCR ONNX PP-OCRv5 mobile（可选增强）：识别更准（斜体/小字）、慢 2~4 倍；不可用自动回退 windows。</summary>
    public const string Paddle = "paddle";

    /// <summary>未知值一律按默认 windows（旧配置缺字段向后兼容，14.9.3）。</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Paddle, StringComparison.OrdinalIgnoreCase) ? Paddle : Windows;
}
