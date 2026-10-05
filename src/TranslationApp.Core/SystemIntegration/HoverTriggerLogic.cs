namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 悬停取词浮标的显隐决策（FR-036 / spec §2.1）。判定分两段，因为这两段跑在不同的地方：
/// <list type="number">
/// <item><see cref="TryAcceptMouseUp"/>：纯算术、无副作用，跑在鼠标钩子线程上，只回答「这次抬起值不值得再去看一眼选区」。
/// 钩子线程上绝不能做重活——低级钩子回调超过系统时限会被摘掉，连侧键映射一起失效。</item>
/// <item><see cref="ConfirmAsync"/>：请 <see cref="ISelectionProbe"/> 去问系统「这儿到底选没选中文字」，
/// 这一步是跨进程调用（可能几十毫秒，也可能很久），所以必须是 await 而不是阻塞。</item>
/// </list>
/// 只有探针明确回答「选中了」才落印：浮标的出现要由「文字被选中」驱动，
/// 而不是由「鼠标动过」驱动——后者会让用户在做任何拖拽时都被打扰。
/// </summary>
public sealed class HoverTriggerLogic(Func<long> nowMs, ISelectionProbe? selectionProbe = null)
{
    /// <summary>按下→抬起的最小位移（物理像素）：低于它判为单击而非拖拽选择。</summary>
    public const double MinDragDistancePx = 12;

    /// <summary>拖拽时长上限：超过它判为长按菜单等其他操作。</summary>
    public const long MaxDragDurationMs = 3_000;

    /// <summary>展示冷却：一次弹出后此时长内不再弹（连续分段选词时防刷屏）。</summary>
    public const long CooldownMs = 800;

    /// <summary>双击判定的两次按下间隔上限：双击选词同样算「选中了文字」。</summary>
    public const long DoubleClickWindowMs = 500;

    /// <summary>双击判定的两次按下位移上限（物理像素）。</summary>
    public const double DoubleClickSlopPx = 4;

    private double _downX;
    private double _downY;
    private long _downAtMs;
    private bool _downOnSelfWindow;
    private bool _downValid;
    private bool _pressLookedLikeDoubleClick;
    private bool _pending;
    private int _generation;
    private long? _lastShowAtMs;

    private double _previousClickX;
    private double _previousClickY;
    private long _previousClickAtMs = long.MinValue;
    private bool _hasPreviousClick;

    /// <summary>左键按下（坐标为物理像素）。总是一次记下最新按下点，作为抬起判定的基准。</summary>
    public void OnMouseDown(double x, double y, bool foregroundIsSelf)
    {
        var now = nowMs();
        _pressLookedLikeDoubleClick = _hasPreviousClick
            && now - _previousClickAtMs <= DoubleClickWindowMs
            && Math.Abs(x - _previousClickX) <= DoubleClickSlopPx
            && Math.Abs(y - _previousClickY) <= DoubleClickSlopPx;

        _previousClickX = x;
        _previousClickY = y;
        _previousClickAtMs = now;
        _hasPreviousClick = true;

        _downX = x;
        _downY = y;
        _downAtMs = now;
        _downOnSelfWindow = foregroundIsSelf;
        _downValid = true;

        _generation++; // 又按下了：上一轮还在等探针的那次作废
        _pending = false;
    }

    /// <summary>
    /// 作废这一次按下：左键落在浮标自己身上（用户是去点印翻译，不是在选词）。
    /// 连双击记忆一起清掉——否则点浮标那一下会被抬起判定当成「双击第二拍」，
    /// 探针一问选区还在，刚落下的印立刻原地重落一枚。
    /// </summary>
    public void IgnorePress()
    {
        _downValid = false;
        _pressLookedLikeDoubleClick = false;
        _pending = false;
        _hasPreviousClick = false;
        _generation++;
    }

    /// <summary>
    /// 第一段：左键抬起的启发式判定，返回 true = 「候选」，值得再请探针确认一次。
    /// 双击的第二拍（位移几乎为零）也算候选——双击选词是最常见的选词方式之一。
    /// </summary>
    public bool TryAcceptMouseUp(double x, double y, bool foregroundIsSelf)
    {
        var hadPress = _downValid;
        var lookedLikeDoubleClick = _pressLookedLikeDoubleClick;
        _downValid = false;
        _pressLookedLikeDoubleClick = false;

        if (!hadPress || foregroundIsSelf || _downOnSelfWindow)
        {
            return false; // 前台是本程序（小窗/设置内划字）一律不弹
        }

        var now = nowMs();
        if (now - _downAtMs > MaxDragDurationMs)
        {
            return false;
        }

        var distance = Math.Sqrt((x - _downX) * (x - _downX) + (y - _downY) * (y - _downY));
        if (distance < MinDragDistancePx && !lookedLikeDoubleClick)
        {
            return false;
        }

        _pending = true;
        return true;
    }

    /// <summary>
    /// 第二段：候选 + 探针确认「确实选中了文字」，才建议落印。
    /// 探针报 <see cref="SelectionProbeResult.Selected"/> 之外的一切结论（None / Unknown）都不落印：
    /// 问不出来时宁可少弹一次，也不要退回「鼠标动过就弹」。等待探针期间用户又按下了（代次变了）也作废。
    /// </summary>
    public async Task<bool> ConfirmAsync(int physicalX, int physicalY)
    {
        if (!_pending)
        {
            return false;
        }

        _pending = false;
        var generation = _generation;

        if (selectionProbe is not null)
        {
            SelectionProbeResult result;
            try
            {
                result = await selectionProbe.ProbeAsync(
                    new SelectionProbeRequest((int)_downX, (int)_downY, physicalX, physicalY));
            }
            catch
            {
                result = SelectionProbeResult.Unknown; // 探针坏掉不该把钩子线程带走
            }

            if (generation != _generation || result != SelectionProbeResult.Selected)
            {
                return false;
            }
        }

        if (generation != _generation)
        {
            return false;
        }

        var now = nowMs();
        if (_lastShowAtMs is { } last && now - last < CooldownMs)
        {
            return false;
        }

        _lastShowAtMs = now;
        return true;
    }
}
