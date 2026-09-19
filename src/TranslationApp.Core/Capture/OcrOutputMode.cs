namespace TranslationApp.Core.Capture;

/// <summary>
/// 截图识别后的处理方式（FR-027 / 14.3.8，落盘字段 = 14.6 的 <c>OcrOutputMode</c>）：
/// <list type="bullet">
/// <item><c>pin</c>（默认）：钉图——图片钉在屏幕上原选区位置，原位显示译文（本阶段批 4a 只钉图）；</item>
/// <item><c>text</c>：旧链路——识别文本填入翻译小窗（FR-021 的全部 AC 在此值下仍然成立，一字不改）；</item>
/// <item><c>both</c>：先钉图再开小窗（注意：点击钉图会让前台变化，小窗按 FR-003 在 200 ms 后自动隐藏）。</item>
/// </list>
/// </summary>
public static class OcrOutputMode
{
    /// <summary>钉图（默认）。</summary>
    public const string Pin = "pin";

    /// <summary>旧链路：文本进小窗。</summary>
    public const string Text = "text";

    /// <summary>先钉图再开小窗。</summary>
    public const string Both = "both";

    /// <summary>未知/空值一律回落 <c>pin</c>（默认值），保证旧配置升级后行为可预期。</summary>
    public static string Parse(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Text => Text,
            Both => Both,
            _ => Pin,
        };

    /// <summary>是否需要钉图（pin / both）。</summary>
    public static bool PinsImage(string? mode) => Parse(mode) is Pin or Both;

    /// <summary>是否需要把识别文本送进翻译小窗（text / both）。</summary>
    public static bool UsesQuickWindow(string? mode) => Parse(mode) is Text or Both;
}
