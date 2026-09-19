using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// FR-027 的「透明度（14.3.6：`Ctrl+滚轮` 步进 0.1、范围 [0.3, 1.0]）+ 右键菜单「恢复不透明」」的规则单测：
/// 步进/夹取/边界反馈依据是纯函数（<see cref="PinLayout"/>），可用性判定与工具条共用
/// <see cref="PinToolbarRules"/>，因此这些真机行为不靠肉眼判断。
/// </summary>
public sealed class PinOpacityTests
{
    [Fact]
    public void 透明度步进为0点1且夹取在0点3到1点0之间()
    {
        Assert.Equal(0.9, PinLayout.StepOpacity(1.0, -120));
        Assert.Equal(1.0, PinLayout.StepOpacity(0.9, 120));
        Assert.Equal(0.3, PinLayout.StepOpacity(0.3, -120)); // 下限：再降也不越过
        Assert.Equal(0.3, PinLayout.StepOpacity(0.4, -240)); // 一次两格
    }

    [Fact]
    public void 透明度步进不产生浮点噪声且零增量保持原值()
    {
        var opacity = 0.3;
        for (var i = 0; i < 7; i++)
        {
            opacity = PinLayout.StepOpacity(opacity, 120);
        }

        Assert.Equal(1.0, opacity);
        Assert.True(PinLayout.IsFullyOpaque(opacity));

        Assert.Equal(0.6, PinLayout.StepOpacity(0.6, 0));
    }

    [Fact]
    public void 透明度夹取与到顶判定覆盖非法输入()
    {
        Assert.Equal(1.0, PinLayout.ClampOpacity(double.NaN));
        Assert.Equal(1.0, PinLayout.ClampOpacity(2.0));
        Assert.Equal(0.3, PinLayout.ClampOpacity(0.05));
        Assert.True(PinLayout.IsFullyOpaque(1.0));
        Assert.False(PinLayout.IsFullyOpaque(0.9));
    }

    [Fact]
    public void 恢复不透明_已不透明时禁用并说明原因_其余情况可用()
    {
        var opaque = PinAvailability.ImageOnly; // AtFullOpacity 默认 true
        var blocked = PinToolbarRules.Resolve(PinToolbarAction.ResetOpacity, opaque);
        Assert.False(blocked.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(blocked.DisabledReason));

        var translucent = new PinAvailability(true, true, false, true, AtFullOpacity: false);
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ResetOpacity, translucent).Enabled);
        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.ResetOpacity, translucent));
    }

    [Fact]
    public void 新增透明度动作不影响既有动作的判定()
    {
        var translucent = new PinAvailability(true, true, false, true, AtFullOpacity: false);
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.Close, translucent).Enabled);
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ZoomIn, translucent).Enabled);
        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, translucent).Enabled);
        Assert.False(string.IsNullOrWhiteSpace(
            PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, translucent).DisabledReason));
    }

    [Fact]
    public void 恢复不透明有键盘入口Ctrl加O()
    {
        Assert.Equal(PinToolbarAction.ResetOpacity, PinShortcuts.Resolve(PinKey.O, ctrl: true, false, false));
        Assert.Null(PinShortcuts.Resolve(PinKey.O, ctrl: false, false, false));
        Assert.Null(PinShortcuts.Resolve(PinKey.O, ctrl: true, shift: true, alt: false)); // Shift/Alt 仍不占用
    }

    [Fact]
    public void 滚轮步进夹取在1点02到2点0之间_非法值回落默认1点1()
    {
        Assert.Equal(PinLayout.DefaultZoomStep, PinLayout.ClampZoomStep(double.NaN));
        Assert.Equal(PinLayout.DefaultZoomStep, PinLayout.ClampZoomStep(1.1));
        Assert.Equal(PinLayout.MinZoomStep, PinLayout.ClampZoomStep(1.0)); // ≤1 会让滚轮方向失去意义
        Assert.Equal(PinLayout.MaxZoomStep, PinLayout.ClampZoomStep(9.0));
    }
}
