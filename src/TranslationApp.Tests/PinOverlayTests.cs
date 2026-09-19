using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 钉图的原文/译文切换与工具条规则（FR-027 / 14.3.6 / 14.3.9）：
/// 默认层与翻转、覆盖层可见性与不透明度映射、工具条按钮的启用/禁用与原因、快捷键映射表、
/// 2.5 s 自动淡出的时序判定、窄窗口里工具条的摆放——全部是 <see cref="PinOverlayRules"/>、
/// <see cref="PinToolbarRules"/>、<see cref="PinShortcuts"/> 里的纯逻辑，因此这些用例不需要任何窗口即可穷举。
/// </summary>
public sealed class PinOverlayTests
{
    // ---------------- 切换状态的默认值与翻转 ----------------

    [Fact]
    public void Default_无译文时_显示原文()
    {
        Assert.Equal(PinTextLayer.Original, PinOverlayRules.Default(hasTranslation: false, preferTranslation: true));
    }

    [Fact]
    public void Default_有译文且设置优先译文_显示译文()
    {
        Assert.Equal(PinTextLayer.Translation, PinOverlayRules.Default(hasTranslation: true, preferTranslation: true));
    }

    [Fact]
    public void Default_有译文但设置不优先_仍显示原文()
    {
        // 14.3.8：pin 模式下 OcrInPlaceReplace 关闭时钉图先显示原文，点工具条才看译文
        Assert.Equal(PinTextLayer.Original, PinOverlayRules.Default(hasTranslation: true, preferTranslation: false));
    }

    [Fact]
    public void Toggle_原文与译文来回翻转()
    {
        var first = PinOverlayRules.Toggle(PinTextLayer.Original);
        var second = PinOverlayRules.Toggle(first);

        Assert.Equal(PinTextLayer.Translation, first);
        Assert.Equal(PinTextLayer.Original, second);
    }

    [Fact]
    public void Toggle_无共享状态_同一输入恒得同一结果()
    {
        // 切换状态只存在各张钉图自己的字段里（不写回设置、不跨张共享）：同一输入必须恒得同一输出
        Assert.Equal(PinOverlayRules.Toggle(PinTextLayer.Original), PinOverlayRules.Toggle(PinTextLayer.Original));
        Assert.Equal(PinOverlayRules.Toggle(PinTextLayer.Translation), PinOverlayRules.Toggle(PinTextLayer.Translation));
        Assert.Equal(PinTextLayer.Original, PinOverlayRules.Toggle(PinTextLayer.Translation));
    }

    // ---------------- 切换对覆盖层可见性的映射 ----------------

    [Theory]
    [InlineData(PinTextLayer.Original, 0.0)]
    [InlineData(PinTextLayer.Translation, 1.0)]
    public void TargetOpacity_原文0译文1(PinTextLayer layer, double expected)
    {
        Assert.Equal(expected, PinOverlayRules.TargetOpacity(layer), 10);
    }

    [Fact]
    public void IsOverlayVisible_与目标不透明度一致()
    {
        Assert.False(PinOverlayRules.IsOverlayVisible(PinTextLayer.Original));
        Assert.True(PinOverlayRules.IsOverlayVisible(PinTextLayer.Translation));
        Assert.Equal(
            PinOverlayRules.TargetOpacity(PinTextLayer.Translation) > 0,
            PinOverlayRules.IsOverlayVisible(PinTextLayer.Translation));
    }

    // ---------------- 覆盖块文字颜色（浅底配深字 / 深底配浅字，不跟主题） ----------------

    [Theory]
    [InlineData(0xFFFFFFFFu, true)]
    [InlineData(0xFFF2F4F7u, true)]
    [InlineData(0xFF1C1F23u, false)]
    [InlineData(0xFF000000u, false)]
    [InlineData(0xFF6F7780u, false)]
    public void IsLightBackground_按相对亮度判定(uint argb, bool expected)
    {
        Assert.Equal(expected, PinOverlayRules.IsLightBackground(argb));
    }

    [Fact]
    public void RelativeLuminance_黑白两端符合定义()
    {
        Assert.Equal(0.0, PinOverlayRules.RelativeLuminance(0xFF000000), 6);
        Assert.Equal(1.0, PinOverlayRules.RelativeLuminance(0xFFFFFFFF), 6);
    }

    [Fact]
    public void TextColorToken_浅底用深字令牌深底用浅字令牌()
    {
        Assert.Equal("Brush.Overlay.TextOnLight", PinOverlayRules.TextColorToken(0xFFFFFFFF));
        Assert.Equal("Brush.Overlay.TextOnDark", PinOverlayRules.TextColorToken(0xFF000000));
    }

    [Fact]
    public void TextColorToken_底色未知时用浅字令牌()
    {
        // 取样失败 → 界面用 CoverFallback（中性灰 #6F7780），灰底上浅字可读性更好
        Assert.Equal("Brush.Overlay.TextOnDark", PinOverlayRules.TextColorToken(null));
    }

    // ---------------- 占位内容（批 4c 只换数据） ----------------

    [Fact]
    public void Placeholder_有覆盖块且落在图像范围内()
    {
        var content = PinContent.Placeholder(800, 400);

        var block = Assert.Single(content.Blocks);
        Assert.True(block.Rect.X >= 0 && block.Rect.Y >= 0);
        Assert.True(block.Rect.Right <= 800 && block.Rect.Bottom <= 400);
        Assert.False(block.Rect.IsEmpty);
        Assert.Equal(PinContent.PlaceholderText, block.Text);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    [InlineData(3, 400)]
    public void Placeholder_极小图像也给出至少1像素且在界内(int width, int height)
    {
        var content = PinContent.Placeholder(width, height);

        var block = Assert.Single(content.Blocks);
        Assert.True(block.Rect.Width >= 1 && block.Rect.Height >= 1);
        Assert.True(block.Rect.Right <= width && block.Rect.Bottom <= height);
    }

    [Fact]
    public void Placeholder_没有译文与原文_故复制禁用()
    {
        var content = PinContent.Placeholder(200, 100);

        Assert.True(content.HasOverlay);
        Assert.False(content.HasTranslation);
        Assert.Null(content.TranslatedText);
    }

    [Fact]
    public void Empty_没有覆盖内容()
    {
        Assert.False(PinContent.Empty.HasOverlay);
        Assert.False(PinContent.Empty.HasTranslation);
    }

    [Fact]
    public void HasTranslation_空白译文不算有译文()
    {
        Assert.False(new PinContent([], TranslatedText: "   ").HasTranslation);
        Assert.True(new PinContent([], TranslatedText: "你好").HasTranslation);
    }

    // ---------------- 工具条按钮的启用/禁用条件 ----------------

    private static PinAvailability TranslationReady { get; } =
        new(HasOverlay: true, HasTranslation: true, OverlayShown: false, AtDefaultZoom: true);

    [Fact]
    public void 关闭恒可用()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.Close, PinAvailability.ImageOnly);

        Assert.True(state.Enabled);
        Assert.Null(state.DisabledReason);
    }

    [Fact]
    public void 无译文时_复制禁用并给出原因()
    {
        var availability = TranslationReady with { HasTranslation = false };
        var state = PinToolbarRules.Resolve(PinToolbarAction.CopyTranslation, availability);

        Assert.False(state.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(state.DisabledReason));
    }

    [Fact]
    public void 有译文时_复制可用()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.CopyTranslation, TranslationReady);

        Assert.True(state.Enabled);
        Assert.Null(state.DisabledReason);
    }

    [Fact]
    public void 无覆盖内容时_切换禁用并给出原因()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ToggleTextLayer, PinAvailability.ImageOnly);

        Assert.False(state.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(state.DisabledReason));
    }

    [Fact]
    public void 有覆盖内容时_切换可用()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ToggleTextLayer, TranslationReady);

        Assert.True(state.Enabled);
    }

    [Fact]
    public void 已是100时_重置禁用并给出原因()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, TranslationReady);

        Assert.False(state.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(state.DisabledReason));
    }

    [Fact]
    public void 非100时_重置可用()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, TranslationReady with { AtDefaultZoom = false });

        Assert.True(state.Enabled);
    }

    [Fact]
    public void 缩放动作恒可用_到达极限由边界提示说明()
    {
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ZoomIn, PinAvailability.ImageOnly).Enabled);
        Assert.True(PinToolbarRules.Resolve(PinToolbarAction.ZoomOut, PinAvailability.ImageOnly).Enabled);
    }

    [Fact]
    public void 每个动作都有明确结论_禁用必须带原因()
    {
        foreach (var action in Enum.GetValues<PinToolbarAction>())
        {
            foreach (var availability in new[] { PinAvailability.ImageOnly, TranslationReady })
            {
                var state = PinToolbarRules.Resolve(action, availability);
                if (!state.Enabled)
                {
                    Assert.False(string.IsNullOrWhiteSpace(state.DisabledReason),
                        $"{action} 被禁用时必须说明原因（不静默无效）");
                }
            }
        }
    }

    // ---------------- 激活态（状态指示：显示译文时点亮切换按钮） ----------------

    [Fact]
    public void IsToggleOn_仅在显示译文时点亮切换按钮()
    {
        Assert.True(PinToolbarRules.IsToggleOn(
            PinToolbarAction.ToggleTextLayer, TranslationReady with { OverlayShown = true }));
        Assert.False(PinToolbarRules.IsToggleOn(
            PinToolbarAction.ToggleTextLayer, TranslationReady with { OverlayShown = false }));
    }

    [Fact]
    public void IsToggleOn_其他按钮没有激活态()
    {
        var shown = TranslationReady with { OverlayShown = true };

        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.CopyTranslation, shown));
        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.Close, shown));
        Assert.False(PinToolbarRules.IsToggleOn(PinToolbarAction.ResetZoom, shown));
    }

    [Fact]
    public void FormatZoom_按倍数显示百分比()
    {
        Assert.Equal("100%", PinToolbarRules.FormatZoom(1.0));
        Assert.Equal("25%", PinToolbarRules.FormatZoom(0.25));
        Assert.Equal("110%", PinToolbarRules.FormatZoom(1.1));
        Assert.Equal("400%", PinToolbarRules.FormatZoom(4.0));
    }

    [Fact]
    public void FormatZoom_非法倍数退化为100()
    {
        Assert.Equal("100%", PinToolbarRules.FormatZoom(double.NaN));
        Assert.Equal("100%", PinToolbarRules.FormatZoom(0));
    }

    // ---------------- 工具条出现与自动淡出（时序判定） ----------------

    [Fact]
    public void ShouldShow_激活或鼠标在其上即出现()
    {
        Assert.True(PinToolbarRules.ShouldShow(isActive: true, pointerOverWindow: false));
        Assert.True(PinToolbarRules.ShouldShow(isActive: false, pointerOverWindow: true));
        Assert.False(PinToolbarRules.ShouldShow(isActive: false, pointerOverWindow: false));
    }

    [Fact]
    public void ResolveOpacity_不显示时为0()
    {
        Assert.Equal(0.0, PinToolbarRules.ResolveOpacity(autoFade: true, show: false, idleMs: 0, pointerOverToolbar: false), 6);
        Assert.Equal(0.0, PinToolbarRules.ResolveOpacity(autoFade: true, show: false, idleMs: 99999, pointerOverToolbar: true), 6);
    }

    [Fact]
    public void ResolveOpacity_开启自动淡出且空闲超过2点5秒_淡到35()
    {
        var opacity = PinToolbarRules.ResolveOpacity(
            autoFade: true, show: true, idleMs: PinToolbarRules.AutoFadeDelayMs, pointerOverToolbar: false);

        Assert.Equal(0.35, opacity, 6);
        Assert.Equal(0.35, PinToolbarRules.FadedOpacity, 6);
    }

    [Fact]
    public void ResolveOpacity_空闲未满阈值仍为不透明()
    {
        var opacity = PinToolbarRules.ResolveOpacity(
            autoFade: true, show: true, idleMs: PinToolbarRules.AutoFadeDelayMs - 1, pointerOverToolbar: false);

        Assert.Equal(1.0, opacity, 6);
    }

    [Fact]
    public void ResolveOpacity_鼠标移入工具条立即恢复不透明()
    {
        var opacity = PinToolbarRules.ResolveOpacity(
            autoFade: true, show: true, idleMs: PinToolbarRules.AutoFadeDelayMs * 10, pointerOverToolbar: true);

        Assert.Equal(1.0, opacity, 6);
    }

    [Fact]
    public void ResolveOpacity_关闭自动淡出则恒为不透明()
    {
        var opacity = PinToolbarRules.ResolveOpacity(
            autoFade: false, show: true, idleMs: PinToolbarRules.AutoFadeDelayMs * 10, pointerOverToolbar: false);

        Assert.Equal(1.0, opacity, 6);
    }

    [Fact]
    public void 淡出常量与文档一致()
    {
        Assert.Equal(2500, PinToolbarRules.AutoFadeDelayMs);
        Assert.Equal(0.35, PinToolbarRules.FadedOpacity, 6);
        Assert.Equal(8, PinToolbarRules.MarginDip, 6);
        Assert.Equal(0.5, PinToolbarRules.MinScale, 6);
    }

    // ---------------- 工具条摆放（水平居中 / 窄窗口按比例缩小） ----------------

    [Fact]
    public void ResolvePlacement_宽度足够时水平居中()
    {
        var placement = PinToolbarRules.ResolvePlacement(
            windowWidth: 400, windowHeight: 300, toolbarWidth: 220, toolbarHeight: 32, margin: 8);

        Assert.True(placement.Visible);
        Assert.Equal(1.0, placement.Scale, 6);
        Assert.Equal(90, placement.OffsetX, 6);
    }

    [Fact]
    public void ResolvePlacement_窄窗口按比例缩小并保持在边距内()
    {
        var placement = PinToolbarRules.ResolvePlacement(
            windowWidth: 200, windowHeight: 300, toolbarWidth: 220, toolbarHeight: 32, margin: 8);

        Assert.True(placement.Visible);
        Assert.Equal(184.0 / 220.0, placement.Scale, 6);
        Assert.Equal(8, placement.OffsetX, 6);
        Assert.True(placement.OffsetX + (220 * placement.Scale) <= 200 - 8 + 0.001);
    }

    [Fact]
    public void ResolvePlacement_缩到下限仍可见()
    {
        // 可用宽度 = toolbarWidth / 2 恰为 MinScale，仍然显示
        var placement = PinToolbarRules.ResolvePlacement(
            windowWidth: 110, windowHeight: 300, toolbarWidth: 188, toolbarHeight: 32, margin: 8);

        Assert.True(placement.Visible);
        Assert.Equal(PinToolbarRules.MinScale, placement.Scale, 6);
    }

    [Fact]
    public void ResolvePlacement_过窄时整体隐藏()
    {
        // 可用宽度 84，只能缩到 0.38（< 0.5）：压成迷你控件既看不清也点不准 → 隐藏（快捷键仍可用）
        var placement = PinToolbarRules.ResolvePlacement(
            windowWidth: 100, windowHeight: 300, toolbarWidth: 220, toolbarHeight: 32, margin: 8);

        Assert.False(placement.Visible);
    }

    [Fact]
    public void ResolvePlacement_图片太矮时隐藏_避免工具条压住整张图()
    {
        // 高度只有 26：放不下（32 × 0.5 + 8 = 24 也只是勉强）——这里连缩小后的高度都超了
        var placement = PinToolbarRules.ResolvePlacement(
            windowWidth: 400, windowHeight: 20, toolbarWidth: 220, toolbarHeight: 32, margin: 8);

        Assert.False(placement.Visible);
    }

    [Theory]
    [InlineData(0, 300, 220, 32, 8)]
    [InlineData(400, 0, 220, 32, 8)]
    [InlineData(400, 300, 0, 32, 8)]
    [InlineData(400, 300, 220, 0, 8)]
    [InlineData(400, 300, 220, 32, -1)]
    [InlineData(-10, 300, 220, 32, 8)]
    public void ResolvePlacement_非法输入不显示也不抛异常(
        double windowWidth, double windowHeight, double toolbarWidth, double toolbarHeight, double margin)
    {
        var placement = PinToolbarRules.ResolvePlacement(windowWidth, windowHeight, toolbarWidth, toolbarHeight, margin);

        Assert.False(placement.Visible);
    }

    // ---------------- 快捷键映射表 ----------------

    [Fact]
    public void Esc关闭且不与Ctrl组合冲突()
    {
        Assert.Equal(PinToolbarAction.Close, PinShortcuts.Resolve(PinKey.Escape, ctrl: false, shift: false, alt: false));
        Assert.Null(PinShortcuts.Resolve(PinKey.Escape, ctrl: true, shift: false, alt: false));
    }

    [Theory]
    [InlineData(PinKey.Space)]
    [InlineData(PinKey.T)]
    public void 空格与T都切换原文译文(PinKey key)
    {
        Assert.Equal(PinToolbarAction.ToggleTextLayer, PinShortcuts.Resolve(key, false, false, false));
    }

    [Fact]
    public void 无修饰键的字母不占用()
    {
        Assert.Null(PinShortcuts.Resolve(PinKey.C, ctrl: false, shift: false, alt: false));
    }

    [Fact]
    public void CtrlC复制译文()
    {
        Assert.Equal(PinToolbarAction.CopyTranslation, PinShortcuts.Resolve(PinKey.C, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public void Ctrl0回100()
    {
        Assert.Equal(PinToolbarAction.ResetZoom, PinShortcuts.Resolve(PinKey.Digit0, ctrl: true, shift: false, alt: false));
    }

    [Theory]
    [InlineData(PinKey.Plus, PinToolbarAction.ZoomIn)]
    [InlineData(PinKey.Minus, PinToolbarAction.ZoomOut)]
    public void Ctrl加减号缩放(PinKey key, PinToolbarAction expected)
    {
        Assert.Equal(expected, PinShortcuts.Resolve(key, ctrl: true, shift: false, alt: false));
    }

    [Fact]
    public void 带Shift或Alt的组合一律不占用()
    {
        Assert.Null(PinShortcuts.Resolve(PinKey.Escape, false, shift: true, alt: false));
        Assert.Null(PinShortcuts.Resolve(PinKey.Space, false, false, alt: true));
        Assert.Null(PinShortcuts.Resolve(PinKey.C, true, shift: true, alt: false));
    }

    [Fact]
    public void 未知键不处理()
    {
        Assert.Null(PinShortcuts.Resolve(PinKey.None, false, false, false));
        Assert.Null(PinShortcuts.Resolve(PinKey.T, true, false, false));
    }

    [Fact]
    public void 每个动作都有键盘入口()
    {
        var combos = new List<PinToolbarAction?>();
        foreach (var key in Enum.GetValues<PinKey>())
        {
            combos.Add(PinShortcuts.Resolve(key, ctrl: false, shift: false, alt: false));
            combos.Add(PinShortcuts.Resolve(key, ctrl: true, shift: false, alt: false));
        }

        foreach (var action in Enum.GetValues<PinToolbarAction>())
        {
            Assert.Contains((PinToolbarAction?)action, combos);
        }

        Assert.Contains((PinToolbarAction?)PinToolbarAction.Close, combos); // Esc 关闭仍然在（AC 7 不回归）
    }
}
