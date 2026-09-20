namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 双击修饰键检测（FR-038 / spec §1）：纯状态机、时钟注入、可单测。
/// 「按下-抬起-再按下同一修饰键」且间隔 ≤ 窗口才算双击；按住修饰键期间混入任何其他键
/// （Alt+Tab / Alt+Space）本次作废——这是与用户日常组合键共存的关键。触发后进入冷却。
/// 钩子层负责提供事件与前台归属，本类不碰系统 API。
/// </summary>
public sealed class ModifierKeyDoubleTapDetector(
    Func<long> nowMs,
    int targetVirtualKey,
    Action onTriggered,
    long windowMs = 250,   // = DefaultWindowMs（主构造函数默认值引用不了自身 const，用字面量）
    long cooldownMs = 500) // = DefaultCooldownMs
{
    /// <summary>两次按下的最大间隔（v2 文档 B1-1 的实测经验值 250ms）。</summary>
    public const long DefaultWindowMs = 250;

    /// <summary>触发后的冷却：连点 Alt 不会连环触发划词。</summary>
    public const long DefaultCooldownMs = 500;

    private bool _firstDown;      // 第一次按下尚未抬起
    private bool _firstCompleted; // 第一次按下已完整「按下-抬起」，等待第二次
    private long _firstUpAtMs;
    private bool _otherKeyDuringHold;
    private long? _lastFiredAtMs;

    /// <summary>目标修饰键之外的键码一律不影响判定，只在按住目标键时作废用。</summary>
    public void OnOtherKeyDown()
    {
        if (_firstDown)
        {
            _otherKeyDuringHold = true;
        }
    }

    public void OnKeyDown(int virtualKey, bool foregroundIsSelf = false)
    {
        if (virtualKey != targetVirtualKey)
        {
            OnOtherKeyDown();
            return;
        }

        if (foregroundIsSelf)
        {
            Reset(); // 在自己的窗口里敲 Alt 不算触发
            return;
        }

        if (_firstDown)
        {
            return; // 同一物理按下的重复消息（auto-repeat）：忽略
        }

        var now = nowMs();

        if (_firstCompleted)
        {
            var gap = now - _firstUpAtMs;
            _firstCompleted = false;
            var inCooldown = _lastFiredAtMs is { } last && now - last < cooldownMs;

            if (gap <= windowMs && !_otherKeyDuringHold && !inCooldown)
            {
                _lastFiredAtMs = now;
                Reset();
                onTriggered();
                return;
            }

            // 超窗/冷却中：这次按下成为新的第一次
        }

        _firstDown = true;
        _otherKeyDuringHold = false;
    }

    public void OnKeyUp(int virtualKey)
    {
        if (virtualKey != targetVirtualKey || !_firstDown)
        {
            return;
        }

        _firstDown = false;
        if (_otherKeyDuringHold)
        {
            Reset();
            return;
        }

        _firstCompleted = true;
        _firstUpAtMs = nowMs();
    }

    private void Reset()
    {
        _firstDown = false;
        _firstCompleted = false;
        _otherKeyDuringHold = false;
    }
}
