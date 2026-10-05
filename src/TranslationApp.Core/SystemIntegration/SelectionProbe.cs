namespace TranslationApp.Core.SystemIntegration;

/// <summary>选区探针对「落点处有没有文本选区」的回答（FR-036 的判定依据）。</summary>
public enum SelectionProbeResult
{
    /// <summary>问不出来：探针不可用（UIA 被拦 / 目标应用挂起 / 超时）。按「没有选区」办。</summary>
    Unknown,

    /// <summary>问到了，落点上没有文本选区。</summary>
    None,

    /// <summary>问到了，落点上确实有一段非空文本选区。</summary>
    Selected,
}

/// <summary>
/// 一次选区询问：按下点与抬起点都要问。从右往左拖选时抬起点可能落在文本之外，
/// 只看抬起点会漏判，所以两个点都带上。
/// </summary>
/// <param name="DownX">左键按下的物理像素 X。</param>
/// <param name="DownY">左键按下的物理像素 Y。</param>
/// <param name="UpX">左键抬起的物理像素 X。</param>
/// <param name="UpY">左键抬起的物理像素 Y。</param>
public readonly record struct SelectionProbeRequest(int DownX, int DownY, int UpX, int UpY);

/// <summary>
/// 选区探针（FR-036 / spec §2.1）：把「抬起的地方到底有没有选中文字」这个问题交给实现方。
/// Core 只认这个口子，具体实现（UI Automation）在 App 层——Core 保持无 UI 依赖、可单测。
/// 实现必须是**有界**的：调用方是全局鼠标钩子，绝不能被跨进程的 UIA 调用拖住。
/// </summary>
public interface ISelectionProbe
{
    /// <summary>询问落点处是否有非空文本选区。</summary>
    Task<SelectionProbeResult> ProbeAsync(SelectionProbeRequest request);
}
