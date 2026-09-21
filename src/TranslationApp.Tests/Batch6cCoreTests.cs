using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 批 6c（用户实测反馈修复）：影子跟读跳句、双击修饰键永不命中。
/// 两处都是「代码看着对、运行时不成立」的缺陷，所以把判定节奏与键码归一化抽成纯函数在这里钉住。
/// </summary>
public class Batch6cCoreTests
{
    /// <summary>假时钟：按脚本给出 State，并记录轮询次数。</summary>
    private static (Func<bool> isSpeaking, Func<int, Task> delay, List<int> delays, Func<int> polls) Build(
        params bool[] speakingSequence)
    {
        var index = 0;
        var delays = new List<int>();
        bool Speaking()
        {
            var value = index < speakingSequence.Length ? speakingSequence[index] : false;
            index++;
            return value;
        }

        Task Delay(int ms)
        {
            delays.Add(ms);
            return Task.CompletedTask;
        }

        return (Speaking, Delay, delays, () => index);
    }

    [Fact]
    public async Task WaitSpeechCycle_LateStart_StillWaitsForTheWholeSentence()
    {
        // 入队后的前两次轮询 State 还没变成 Speaking —— 旧实现在这里立刻返回，跟读就抢跑到语音前面
        var (speaking, delay, delays, polls) = Build(false, false, true, true, true, false);

        await TtsService.WaitSpeechCycleAsync(speaking, delay, () => false, pollMs: 60, startWaitMs: 1500);

        // 等开始 2 次 + 确认开始 1 次 + 等结束 3 次 = 6 次采样、4 次等待；旧实现 1 次采样 0 次等待
        Assert.Equal(6, polls());
        Assert.Equal(4, delays.Count);
    }

    [Fact]
    public async Task WaitSpeechCycle_ReturnsOnlyAfterSpeakingStopped()
    {
        var (speaking, delay, delays, polls) = Build(true, true, false);

        await TtsService.WaitSpeechCycleAsync(speaking, delay, () => false, 60, 1500);

        // 立刻开始念：第 1 次采样确认开始，再采样两次才看到停
        Assert.Equal(3, polls());
        Assert.Single(delays);
    }

    [Fact]
    public async Task WaitSpeechCycle_NeverStarted_GivesUpWithinGrace()
    {
        var delays = 0;
        await TtsService.WaitSpeechCycleAsync(
            () => false,
            _ => { delays++; return Task.CompletedTask; },
            () => false,
            pollMs: 60,
            startWaitMs: 300);

        Assert.Equal(5, delays);
    }

    [Fact]
    public async Task WaitSpeechCycle_Cancelled_BailsOutWithoutBusyLooping()
    {
        var delays = 0;
        await TtsService.WaitSpeechCycleAsync(
            () => true,
            _ => { delays++; return Task.CompletedTask; },
            () => true,
            pollMs: 60,
            startWaitMs: 1500);

        Assert.Equal(0, delays);
    }

    [Fact]
    public void MouseButtonHook_XButtonMessageCodes_MatchWin32()
    {
        // 用户日志实测：真侧键按下/抬起送进低级钩子的是 0x020B / 0x020C。
        // 代码里曾写成 0x040B / 0x040C（WM_APP 私有区间，不是鼠标消息），过滤永不命中 → 侧键整体失效。
        Assert.Equal(0x020B, MouseButtonHook.WmXButtonDown);
        Assert.Equal(0x020C, MouseButtonHook.WmXButtonUp);
    }

    [Theory]
    [InlineData(0xA4, 0x12)] // VK_LMENU  -> VK_MENU   （实测左 Alt 双击上报的就是 0xA4）
    [InlineData(0xA5, 0x12)] // VK_RMENU  -> VK_MENU
    [InlineData(0xA2, 0x11)] // VK_LCONTROL -> VK_CONTROL
    [InlineData(0xA3, 0x11)] // VK_RCONTROL -> VK_CONTROL
    [InlineData(0xA0, 0x10)] // VK_LSHIFT -> VK_SHIFT
    [InlineData(0xA1, 0x10)] // VK_RSHIFT -> VK_SHIFT
    [InlineData(0x5C, 0x5B)] // VK_RWIN   -> VK_LWIN
    [InlineData(0x12, 0x12)] // 通用码原样
    [InlineData(0x41, 0x41)] // 普通键不动
    public void NormalizeModifierKey_MapsLeftRightToGeneric(int vk, int expected)
    {
        Assert.Equal(expected, KeyboardButtonHook.NormalizeModifierKey(vk));
    }

    [Theory]
    [InlineData("alt", 0xA4)]
    [InlineData("alt", 0x12)]
    [InlineData("ctrl", 0xA3)]
    [InlineData("shift", 0xA1)]
    [InlineData("win", 0x5C)]
    public void DoubleTapDetector_FiresOnLeftRightModifierCode(string key, int vk)
    {
        var target = key switch
        {
            "ctrl" => 0x11,
            "shift" => 0x10,
            "win" => 0x5B,
            _ => 0x12,
        };
        var normalized = KeyboardButtonHook.NormalizeModifierKey(vk);
        var fired = 0;
        var now = 0L;
        var detector = new ModifierKeyDoubleTapDetector(() => now, () => target, () => fired++);

        // 按下-抬起-再按下-抬起，间隔 120ms：归一化后必须命中
        detector.OnKeyDown(normalized);
        now += 40;
        detector.OnKeyUp(normalized);
        now += 120;
        detector.OnKeyDown(normalized);
        now += 40;
        detector.OnKeyUp(normalized);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void DoubleTapDetector_RawLeftAltCodeNeverMatchesGenericTarget()
    {
        // 这条钉住缺陷本身：不归一化时，左 Alt 的 0xA4 与目标 0x12 永不相等，双击永不触发
        var fired = 0;
        var detector = new ModifierKeyDoubleTapDetector(() => 0L, () => 0x12, () => fired++);

        for (var i = 0; i < 2; i++)
        {
            detector.OnKeyDown(0xA4);
            detector.OnKeyUp(0xA4);
        }

        Assert.Equal(0, fired);
    }
}
