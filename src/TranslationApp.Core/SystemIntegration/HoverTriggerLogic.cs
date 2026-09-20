namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 悬停取词浮标的显隐决策（FR-036 / spec §2.1）：纯逻辑、可单测——
/// 只有「按住左键拖出了一段选择」才建议弹浮标。时钟与前台归属由调用方（钩子层）注入，
/// 本类不碰任何系统 API、无副作用。
/// </summary>
public sealed class HoverTriggerLogic(Func<long> nowMs)
{
    /// <summary>按下→抬起的最小位移（物理像素）：低于它判为单击而非拖拽选择。</summary>
    public const double MinDragDistancePx = 12;

    /// <summary>拖拽时长上限：超过它判为长按菜单等其他操作。</summary>
    public const long MaxDragDurationMs = 3_000;

    /// <summary>展示冷却：一次弹出后此时长内不再弹（连续分段选词时防刷屏）。</summary>
    public const long CooldownMs = 800;

    private double _downX;
    private double _downY;
    private long _downAtMs;
    private bool _downOnSelfWindow;
    private bool _downValid;
    private long? _lastShowAtMs;

    /// <summary>左键按下（坐标为物理像素）。总是一次记下最新按下点，作为抬起判定的基准。</summary>
    public void OnMouseDown(double x, double y, bool foregroundIsSelf)
    {
        _downX = x;
        _downY = y;
        _downAtMs = nowMs();
        _downOnSelfWindow = foregroundIsSelf;
        _downValid = true;
    }

    /// <summary>左键抬起：返回 true = 建议展示浮标（内部同时消费本次按下并记录冷却起点）。</summary>
    public bool OnMouseUp(double x, double y, bool foregroundIsSelf)
    {
        if (!_downValid || foregroundIsSelf || _downOnSelfWindow)
        {
            _downValid = false; // 前台是本程序（小窗/设置内划字）一律不弹
            return false;
        }

        _downValid = false;
        var now = nowMs();
        var distance = Math.Sqrt((x - _downX) * (x - _downX) + (y - _downY) * (y - _downY));
        if (distance < MinDragDistancePx)
        {
            return false;
        }

        if (now - _downAtMs > MaxDragDurationMs)
        {
            return false;
        }

        if (_lastShowAtMs is { } last && now - last < CooldownMs)
        {
            return false;
        }

        _lastShowAtMs = now;
        return true;
    }
}
