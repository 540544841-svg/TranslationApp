namespace TranslationApp.Core.Capture;

/// <summary>
/// PaddleOCR 会话内存策略（FR-030 / 14.9.2）：懒加载（首次识别才建会话，windows 路径零影响）+
/// 空闲自动释放（默认，重建 ~0.5s，C0 实测 InitModels 中位 148 ms）+ 可选常驻（<c>OcrPaddleResident</c>）。
/// 时序判定为纯函数放 Core 以便单测；定时器与 Dispose 留在 App 层适配器。
/// </summary>
public static class PaddleSessionPolicy
{
    /// <summary>
    /// 空闲释放时长：无任何 OCR 调用超过该时长即释放模型会话（14.9.2：时长放 Core 常量，不进设置）。
    /// 常驻期内存口径 < 200MB（第 6 章），释放后回落到启用前水平（AC 6）。
    /// </summary>
    public static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 是否应释放会话：常驻（<paramref name="resident"/> = true）永不释放；从未使用（无会话）不释放；
    /// 距上次使用 ≥ <see cref="IdleReleaseDelay"/> 即释放（恰好到期即释放）。
    /// </summary>
    public static bool ShouldRelease(DateTimeOffset? lastUsedUtc, DateTimeOffset nowUtc, bool resident) =>
        !resident
        && lastUsedUtc is { } lastUsed
        && nowUtc - lastUsed >= IdleReleaseDelay;
}
