using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-036：悬停浮标显隐决策（纯逻辑，时钟注入）。spec §2.1。</summary>
public class HoverTriggerLogicTests
{
    private long _now;

    private HoverTriggerLogic New() => new(() => _now);

    [Fact]
    public void DragOfAtLeast12Px_Shows()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.True(logic.OnMouseUp(160, 100, foregroundIsSelf: false));
    }

    [Fact]
    public void ClickUnderThreshold_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        Assert.False(logic.OnMouseUp(103, 101, foregroundIsSelf: false));
    }

    [Fact]
    public void DragOverThreeSeconds_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 3_001;
        Assert.False(logic.OnMouseUp(200, 100, foregroundIsSelf: false));
    }

    [Fact]
    public void ExactlyThreeSeconds_DoesShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 3_000;
        Assert.True(logic.OnMouseUp(200, 100, foregroundIsSelf: false));
    }

    [Fact]
    public void SecondShowWithinCooldownMs_DoesNotShow_ButLaterDoes()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 100;
        Assert.True(logic.OnMouseUp(200, 100, foregroundIsSelf: false));

        // 冷却 800ms 内的下一次拖拽选择被吞掉
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 700;
        Assert.False(logic.OnMouseUp(200, 100, foregroundIsSelf: false));

        // 冷却结束后恢复
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 2_000;
        Assert.True(logic.OnMouseUp(200, 100, foregroundIsSelf: false));
    }

    [Fact]
    public void SelfForegroundAtPressOrRelease_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: true);
        _now = 150;
        Assert.False(logic.OnMouseUp(200, 100, foregroundIsSelf: false));

        var logic2 = New();
        logic2.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.False(logic2.OnMouseUp(200, 100, foregroundIsSelf: true));
    }

    [Fact]
    public void UpWithoutPressingFirst_DoesNotShow()
    {
        var logic = New();
        _now = 500;
        Assert.False(logic.OnMouseUp(300, 300, foregroundIsSelf: false));
    }

    [Fact]
    public void TwoPressesThenOneUp_UsesLastPressPoint()
    {
        // 连点两下（第二下位移足够）：以最后一次的按下点判定
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 50;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 100;
        Assert.True(logic.OnMouseUp(200, 100, foregroundIsSelf: false));
    }
}
