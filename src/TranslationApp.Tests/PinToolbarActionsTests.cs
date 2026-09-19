using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 批 4c 新增的两个工具条动作（FR-027 / 14.3.2 / 14.3.8）：
/// 「在小窗中打开」需要有原文，「重试」只在翻译失败时可用；
/// 两者都必须给出**禁用原因**（不静默无效），且不影响既有动作的行为。
/// </summary>
public sealed class PinToolbarActionsTests
{
    private static readonly PinAvailability Ready = new(
        HasOverlay: true, HasTranslation: true, OverlayShown: true, AtDefaultZoom: false,
        CanRetry: false, HasSource: true);

    private static readonly PinAvailability Failed = new(
        HasOverlay: false, HasTranslation: false, OverlayShown: false, AtDefaultZoom: true,
        CanRetry: true, HasSource: true);

    [Fact]
    public void 在小窗中打开_有原文时可用_无原文时禁用并说明原因()
    {
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.OpenInQuickWindow, Ready).Enabled);

        var blocked = PinToolbarRules.Resolve(PinToolbarAction.OpenInQuickWindow, PinAvailability.ImageOnly);
        Assert.False(blocked.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(blocked.DisabledReason));
    }

    [Fact]
    public void 重试_只在可重试时可用_其余情况禁用并说明原因()
    {
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.RetryTranslation, Failed).Enabled);

        var blocked = PinToolbarRules.Resolve(PinToolbarAction.RetryTranslation, Ready);
        Assert.False(blocked.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(blocked.DisabledReason));

        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.RetryTranslation, PinAvailability.ImageOnly).Enabled);
    }

    [Fact]
    public void 新增动作不影响既有动作的判定()
    {
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.Close, PinAvailability.ImageOnly).Enabled);
        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.ToggleTextLayer, PinAvailability.ImageOnly).Enabled);
        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.CopyTranslation, PinAvailability.ImageOnly).Enabled);
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ZoomIn, PinAvailability.ImageOnly).Enabled);
        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, PinAvailability.ImageOnly).Enabled);
    }

    [Fact]
    public void 新增动作不是切换按钮_因此没有激活态()
    {
        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.RetryTranslation, Failed));
        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.OpenInQuickWindow, Ready));
        Assert.True(PinToolbarRules.IsToggleOn(PinToolbarAction.ToggleTextLayer, Ready));
    }

    [Fact]
    public void 键盘映射不占用新键位_两个新动作只走按钮()
    {
        Assert.Equal(PinToolbarAction.Close, PinShortcuts.Resolve(PinKey.Escape, false, false, false));
        Assert.Equal(PinToolbarAction.ToggleTextLayer, PinShortcuts.Resolve(PinKey.T, false, false, false));
        Assert.Equal(PinToolbarAction.CopyTranslation, PinShortcuts.Resolve(PinKey.C, true, false, false));
        Assert.Null(PinShortcuts.Resolve(PinKey.T, true, false, false)); // Ctrl+T 不占用
    }

    [Fact]
    public void PinAvailability_默认值让既有测试的构造仍然合法()
    {
        var inherited = new PinAvailability(true, true, false, true);
        Assert.False(inherited.CanRetry);
        Assert.False(inherited.HasSource);
    }

    // ---------------- 内容侧的新字段 ----------------

    [Fact]
    public void PinContent_面板与可重试的新字段默认关闭()
    {
        var content = new PinContent([], TranslatedText: "译文");
        Assert.False(content.HasPanel);
        Assert.False(content.CanRetry);
        Assert.Equal(0, content.PanelHeightDip);
    }

    [Fact]
    public void PinContent_面板文本非空才算有面板_且需要原文才可开小窗()
    {
        var withPanel = new PinContent([], SourceText: "原文") { PanelText = "译文", PanelHeightDip = 120 };
        Assert.True(withPanel.HasPanel);
        Assert.True(withPanel.HasSource);

        Assert.False(new PinContent([], SourceText: "   ").HasSource);
        Assert.False(new PinContent([]) { PanelText = "  " }.HasPanel);
    }

    [Fact]
    public void PinOverlayBlock_换行标记默认为关闭()
    {
        var block = new PinOverlayBlock(new PixelRect(0, 0, 10, 10), "x");
        Assert.False(block.Wrap);

        Assert.True(new PinOverlayBlock(new PixelRect(0, 0, 10, 10), "x", Wrap: true).Wrap);
    }

    // ---------------- 强制翻译（v1.2 修复批 ③-C） ----------------

    [Fact]
    public void 强制翻译_只在跳过态可用_其余情况禁用并说明原因()
    {
        var skipped = Ready with { CanForceTranslate = true };
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ForceTranslate, skipped).Enabled);

        var blocked = PinToolbarRules.Resolve(PinToolbarAction.ForceTranslate, Ready);
        Assert.False(blocked.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(blocked.DisabledReason));
        Assert.False(PinToolbarRules.Resolve(PinToolbarAction.ForceTranslate, PinAvailability.ImageOnly).Enabled);
    }

    [Fact]
    public void 强制翻译有键盘入口_CtrlF_且无修饰键的F不占用()
    {
        Assert.Equal(PinToolbarAction.ForceTranslate, PinShortcuts.Resolve(PinKey.F, true, false, false));
        Assert.Null(PinShortcuts.Resolve(PinKey.F, false, false, false));
    }

    [Fact]
    public void PinContent_强制翻译位默认关闭()
    {
        Assert.False(new PinContent([], SourceText: "原文").CanForceTranslate);
        Assert.True(new PinContent([], SourceText: "原文") { CanForceTranslate = true }.CanForceTranslate);
    }

    // ---------------- 工具条条带摆放（v1.2 修复批 ④） ----------------

    [Fact]
    public void 条带摆放_宽度足够时_水平居中且不缩小()
    {
        var placement = PinToolbarRules.ResolveBandPlacement(400, 200, 8);
        Assert.True(placement.Visible);
        Assert.Equal(1.0, placement.Scale, 10);
        Assert.Equal((400 - 200) / 2.0, placement.OffsetX, 10);
    }

    [Fact]
    public void 条带摆放_窄窗口按比例缩小_且仍居中()
    {
        var placement = PinToolbarRules.ResolveBandPlacement(200, 200, 8);
        Assert.True(placement.Visible);
        Assert.Equal((200 - 16) / 200.0, placement.Scale, 6);
        Assert.Equal(8, placement.OffsetX, 10);
    }

    [Fact]
    public void 条带摆放_缩到下限以下或非法输入时_整体隐藏()
    {
        // 可用宽 20、工具条 200 → scale 0.1 < 0.5 → 隐藏（快捷键与右键菜单仍可用）
        Assert.False(PinToolbarRules.ResolveBandPlacement(40, 200, 10).Visible);
        Assert.False(PinToolbarRules.ResolveBandPlacement(0, 200, 8).Visible);
        Assert.False(PinToolbarRules.ResolveBandPlacement(400, 0, 8).Visible);
    }

    [Fact]
    public void 条带摆放_高度不再参与判定()
    {
        // 旧的浮层摆放会因「图片高度放不下」隐藏工具条；条带在图片外，高度不参与判定
        Assert.True(PinToolbarRules.ResolveBandPlacement(600, 300, 8).Visible);
    }
}
