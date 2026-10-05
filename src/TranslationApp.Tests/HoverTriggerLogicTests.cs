using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-036：悬停浮标显隐决策（纯逻辑，时钟与探针都注入）。spec §2.1。</summary>
public class HoverTriggerLogicTests
{
    private long _now;

    private HoverTriggerLogic New(ISelectionProbe? probe = null) => new(() => _now, probe);

    /// <summary>无探针时走完整两段判定，等价于「只看启发式」（探针不参与）。</summary>
    private static async Task<bool> UpAsync(HoverTriggerLogic logic, double x, double y, bool self = false)
    {
        if (!logic.TryAcceptMouseUp(x, y, self))
        {
            return false;
        }

        return await logic.ConfirmAsync((int)x, (int)y);
    }

    private sealed class FakeProbe(SelectionProbeResult result) : ISelectionProbe
    {
        /// <summary>探针下一次的回答（可在用例中途改，模拟「先拖空、后真选中」）。</summary>
        public SelectionProbeResult Result { get; set; } = result;

        public List<SelectionProbeRequest> Calls { get; } = [];

        public TaskCompletionSource? Gate { get; init; }

        public async Task<SelectionProbeResult> ProbeAsync(SelectionProbeRequest request)
        {
            Calls.Add(request);
            if (Gate is not null)
            {
                await Gate.Task;
            }

            return Result;
        }
    }

    [Fact]
    public async Task DragOfAtLeast12Px_Shows()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.True(await UpAsync(logic, 160, 100));
    }

    [Fact]
    public async Task ClickUnderThreshold_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        Assert.False(await UpAsync(logic, 103, 101));
    }

    [Fact]
    public async Task DragOverThreeSeconds_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 3_001;
        Assert.False(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task ExactlyThreeSeconds_DoesShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 3_000;
        Assert.True(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task SecondShowWithinCooldownMs_DoesNotShow_ButLaterDoes()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 100;
        Assert.True(await UpAsync(logic, 200, 100));

        // 冷却 800ms 内的下一次拖拽选择被吞掉
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 700;
        Assert.False(await UpAsync(logic, 200, 100));

        // 冷却结束后恢复
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 2_000;
        Assert.True(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task SelfForegroundAtPressOrRelease_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: true);
        _now = 150;
        Assert.False(await UpAsync(logic, 200, 100));

        var logic2 = New();
        logic2.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.False(await UpAsync(logic2, 200, 100, self: true));
    }

    [Fact]
    public async Task UpWithoutPressingFirst_DoesNotShow()
    {
        var logic = New();
        _now = 500;
        Assert.False(await UpAsync(logic, 300, 300));
    }

    [Fact]
    public async Task TwoPressesThenOneUp_UsesLastPressPoint()
    {
        // 连点两下（第二下位移足够）：以最后一次的按下点判定
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 50;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 100;
        Assert.True(await UpAsync(logic, 200, 100));
    }

    // ==================== 选区探针：浮标只由「文字被选中」驱动 ====================

    [Fact]
    public async Task ProbeSelected_Shows()
    {
        var probe = new FakeProbe(SelectionProbeResult.Selected);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.True(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task ProbeNone_DoesNotShow()
    {
        var probe = new FakeProbe(SelectionProbeResult.None);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.False(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task ProbeUnknown_DoesNotShow()
    {
        // 问不出来时宁可少弹一次，也不退回「鼠标动过就弹」
        var probe = new FakeProbe(SelectionProbeResult.Unknown);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.False(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task ProbeReceivesBothPressAndReleasePoints()
    {
        var probe = new FakeProbe(SelectionProbeResult.Selected);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.True(await UpAsync(logic, 200, 130));

        var call = Assert.Single(probe.Calls);
        Assert.Equal(100, call.DownX);
        Assert.Equal(100, call.DownY);
        Assert.Equal(200, call.UpX);
        Assert.Equal(130, call.UpY);
    }

    [Fact]
    public async Task ProbeNotConsulted_WhenHeuristicsReject()
    {
        var probe = new FakeProbe(SelectionProbeResult.Selected);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        Assert.False(await UpAsync(logic, 103, 101)); // 位移不够：连问都不问
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task ProbeFailing_DoesNotShow_AndDoesNotThrow()
    {
        var logic = New(new ThrowingProbe());
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.False(await UpAsync(logic, 200, 100));
    }

    [Fact]
    public async Task ProbeNone_DoesNotConsumeCooldown()
    {
        // 拖着玩（没选中）不该吃掉冷却：紧接着真的选中一次仍要落印
        var probe = new FakeProbe(SelectionProbeResult.None);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 100;
        Assert.False(await UpAsync(logic, 200, 100));

        probe.Result = SelectionProbeResult.Selected;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 200; // 离上一次只有 100ms，仍在冷却窗口里
        Assert.True(await UpAsync(logic, 200, 100));
    }

    // ==================== 双击选词：同样是「选中了文字」 ====================

    [Fact]
    public async Task DoubleClickWithSelection_Shows()
    {
        var probe = new FakeProbe(SelectionProbeResult.Selected);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        Assert.False(await UpAsync(logic, 100, 100)); // 第一拍：单击，不算

        _now = 260;
        logic.OnMouseDown(101, 100, foregroundIsSelf: false);
        Assert.True(await UpAsync(logic, 101, 100)); // 第二拍：双击选词，位移几乎为零也算候选
    }

    [Fact]
    public async Task DoubleClickWithoutSelection_DoesNotShow()
    {
        var probe = new FakeProbe(SelectionProbeResult.None);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        await UpAsync(logic, 100, 100);
        _now = 260;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        Assert.False(await UpAsync(logic, 100, 100));
    }

    [Fact]
    public async Task SlowSecondClick_IsNotADoubleClick()
    {
        var probe = new FakeProbe(SelectionProbeResult.Selected);
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        await UpAsync(logic, 100, 100);

        _now = 120 + HoverTriggerLogic.DoubleClickWindowMs + 1;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        Assert.False(await UpAsync(logic, 100, 100)); // 间隔太久：只是又一次单击
    }

    // ==================== 探针在飞的时候用户又按下了 ====================

    [Fact]
    public async Task PressWhileProbeInFlight_InvalidatesShow()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe(SelectionProbeResult.Selected) { Gate = gate };
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;

        Assert.True(logic.TryAcceptMouseUp(200, 100, foregroundIsSelf: false));
        var confirming = logic.ConfirmAsync(200, 100);

        logic.OnMouseDown(300, 300, foregroundIsSelf: false); // 探针还没回来，用户已经又按下去了
        gate.SetResult();
        Assert.False(await confirming);
    }

    // ==================== 点浮标那一下不算选词的起手 ====================

    [Fact]
    public async Task IgnoredPress_ThenUp_DoesNotShow()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        logic.IgnorePress(); // 左键落在浮标上：这一下不是选词的起手
        _now = 150;
        Assert.False(await UpAsync(logic, 160, 100)); // 位移再大也不弹
    }

    [Fact]
    public async Task IgnorePress_ForgetsDoubleClickMemory()
    {
        var logic = New();
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 120;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false); // 第二拍：双击选词
        Assert.True(await UpAsync(logic, 100, 100));

        _now = 700;
        logic.IgnorePress(); // 点浮标：双击记忆一并清掉
        _now = 720;
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        Assert.False(await UpAsync(logic, 100, 100)); // 不再被当成第二拍，浮标不重落
    }

    [Fact]
    public async Task IgnorePress_InvalidatesInFlightProbe()
    {
        var gate = new TaskCompletionSource();
        var probe = new FakeProbe(SelectionProbeResult.Selected) { Gate = gate };
        var logic = New(probe);
        logic.OnMouseDown(100, 100, foregroundIsSelf: false);
        _now = 150;
        Assert.True(logic.TryAcceptMouseUp(200, 100, foregroundIsSelf: false));
        var confirming = logic.ConfirmAsync(200, 100);

        logic.IgnorePress(); // 探针还在飞，用户已经点向浮标
        gate.SetResult();
        Assert.False(await confirming);
    }

    private sealed class ThrowingProbe : ISelectionProbe
    {
        public Task<SelectionProbeResult> ProbeAsync(SelectionProbeRequest request) =>
            throw new InvalidOperationException("UIA 挂了");
    }
}
