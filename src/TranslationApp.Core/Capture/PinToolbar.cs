using System.Globalization;

namespace TranslationApp.Core.Capture;

/// <summary>钉图工具条 / 右键菜单的动作（FR-027 / 14.3.6 / 14.3.9）。键盘与按钮共用同一套动作枚举，两处行为不可能走偏。</summary>
public enum PinToolbarAction
{
    /// <summary>切换原文/译文（空格 / T / 双击 / 工具条按钮）。</summary>
    ToggleTextLayer,

    /// <summary>复制译文（Ctrl+C）。</summary>
    CopyTranslation,

    /// <summary>重置到 100%（Ctrl+0 / 工具条上的倍数按钮）。</summary>
    ResetZoom,

    /// <summary>放大（Ctrl+加号 / 滚轮向上）。</summary>
    ZoomIn,

    /// <summary>缩小（Ctrl+减号 / 滚轮向下）。</summary>
    ZoomOut,

    /// <summary>在翻译小窗中打开（把识别文本带回主流程；14.3.8）。</summary>
    OpenInQuickWindow,

    /// <summary>翻译失败后重试（14.3.2 / AC 12；只在 <see cref="PinAvailability.CanRetry"/> 时可用）。</summary>
    RetryTranslation,

    /// <summary>
    /// 强制翻译（v1.2 修复批 ③-C）：「识别语言与目标语言相同」被跳过时，把源语言置回自动检测重新翻译；
    /// 只在 <see cref="PinAvailability.CanForceTranslate"/> 时可用（工具条「翻译」按钮 + 右键菜单，无快捷键）。
    /// </summary>
    ForceTranslate,

    /// <summary>恢复不透明（14.3.6 右键菜单：把单张钉图的透明度调回 1.0）。</summary>
    ResetOpacity,

    /// <summary>关闭钉图（Esc / 工具条 ✕ / 右键菜单）。</summary>
    Close,
}

/// <summary>某个动作此刻是否可用；禁用时**必须**带上给用户看的原因（不静默无效）。</summary>
public readonly record struct PinActionState(bool Enabled, string? DisabledReason)
{
    /// <summary>可用。</summary>
    public static PinActionState Always { get; } = new(true, null);

    /// <summary>禁用并说明原因（原因会作 ToolTip 显示，键盘触发时也用它作一次性提示）。</summary>
    public static PinActionState Blocked(string reason) => new(false, reason);
}

/// <summary>工具条按钮启用/禁用与激活态的唯一输入（一张钉图此刻的可用性）。</summary>
/// <param name="HasOverlay">是否有覆盖内容可切换。</param>
/// <param name="HasTranslation">是否有可复制的译文。</param>
/// <param name="OverlayShown">当前是否显示译文层（= 切换按钮的激活态）。</param>
/// <param name="AtDefaultZoom">当前是否已是 100%。</param>
/// <param name="CanRetry">翻译失败、可通过重试补齐译文（14.3.2）。</param>
/// <param name="HasSource">有识别出的原文可供「在小窗中打开」（14.3.8）。</param>
/// <param name="AtFullOpacity">当前是否已是不透明（14.3.6 的「恢复不透明」据此禁用并说明原因）。</param>
/// <param name="CanForceTranslate">处于「识别语言与目标语言相同」的跳过态、可强制翻译（v1.2 修复批 ③-C）。</param>
public readonly record struct PinAvailability(
    bool HasOverlay, bool HasTranslation, bool OverlayShown, bool AtDefaultZoom,
    bool CanRetry = false, bool HasSource = false, bool AtFullOpacity = true,
    bool CanForceTranslate = false)
{
    /// <summary>只有图片、没有覆盖内容时的可用性。</summary>
    public static PinAvailability ImageOnly { get; } = new(false, false, false, true);
}

/// <summary>
/// 工具条的出现/淡出、按钮的启用禁用、以及按钮在窗口内的摆放（FR-027 / 14.3.6 / 14.3.9）：
/// 全是纯函数，因此「2.5 s 后淡到 35%」「无译文时复制禁用并给出原因」「窄窗口里工具条按比例缩小或整体隐藏」
/// 这些规则都能单测，而不是只能靠真机肉眼判断。
/// </summary>
public static class PinToolbarRules
{
    /// <summary>交互停止多久后淡出（14.3.6 的 2.5 s）。</summary>
    public const double AutoFadeDelayMs = 2500;

    /// <summary>淡出后的残留不透明度（14.3.6：不隐藏，只是变淡，避免长期遮挡内容）。</summary>
    public const double FadedOpacity = 0.35;

    /// <summary>正常不透明度。</summary>
    public const double FullOpacity = 1.0;

    /// <summary>不可见（窗口非激活且鼠标不在其上）。</summary>
    public const double HiddenOpacity = 0.0;

    /// <summary>工具条与窗口边缘的间距（DIP；14.3.6 的「下方 8 px」，本实现落在图片内底部，见 <see cref="ResolvePlacement"/>）。</summary>
    public const double MarginDip = 8.0;

    /// <summary>窗口窄到工具条只能缩到该比例以下时，整体隐藏（压成迷你控件既看不清也点不准，快捷键仍可用）。</summary>
    public const double MinScale = 0.5;

    /// <summary>
    /// 是否需要把工具条显示出来（14.3.6：鼠标移入或窗口激活即出现）。
    /// 非激活 + 鼠标不在其上 → 不出现，避免在任意背景上平白遮住内容。
    /// </summary>
    public static bool ShouldShow(bool isActive, bool pointerOverWindow) => isActive || pointerOverWindow;

    /// <summary>
    /// 工具条此刻应有的不透明度：
    /// 不显示 → 0；关闭自动淡出 → 恒为 1；鼠标停在工具条上或交互未满 2.5 s → 1；否则 → 0.35。
    /// </summary>
    /// <param name="idleMs">距上一次交互（出现 / 悬停 / 点按 / 切换）的毫秒数。</param>
    public static double ResolveOpacity(bool autoFade, bool show, double idleMs, bool pointerOverToolbar)
    {
        if (!show)
        {
            return HiddenOpacity;
        }

        if (!autoFade || pointerOverToolbar || idleMs < AutoFadeDelayMs)
        {
            return FullOpacity;
        }

        return FadedOpacity;
    }

    /// <summary>
    /// 动作此刻的可用性（工具条按钮的 `IsEnabled` 与「为什么不能用」的唯一依据）：
    /// 关闭恒可用；无覆盖内容不能切换；无译文不能复制；已在 100% 时重置无意义（禁用并说明，而不是点了没反应）；
    /// 已是不透明时「恢复不透明」同样禁用并说明。
    /// 缩放动作恒可用——到达上下限时由边界提示条说明原因（与滚轮同一反馈路径）。
    /// </summary>
    public static PinActionState Resolve(PinToolbarAction action, PinAvailability availability) => action switch
    {
        PinToolbarAction.Close => PinActionState.Always,
        PinToolbarAction.ToggleTextLayer => availability.HasOverlay
            ? PinActionState.Always
            : PinActionState.Blocked("本次钉图没有可切换的覆盖内容"),
        PinToolbarAction.CopyTranslation => availability.HasTranslation
            ? PinActionState.Always
            : PinActionState.Blocked("尚无译文，暂无可复制的内容"),
        PinToolbarAction.ResetZoom => availability.AtDefaultZoom
            ? PinActionState.Blocked("当前已是 100%")
            : PinActionState.Always,
        PinToolbarAction.OpenInQuickWindow => availability.HasSource
            ? PinActionState.Always
            : PinActionState.Blocked("本次钉图没有识别到原文"),
        PinToolbarAction.RetryTranslation => availability.CanRetry
            ? PinActionState.Always
            : PinActionState.Blocked("当前没有需要重试的翻译"),
        PinToolbarAction.ForceTranslate => availability.CanForceTranslate
            ? PinActionState.Always
            : PinActionState.Blocked("当前无需强制翻译（已按识别语言完成翻译或没有原文）"),
        PinToolbarAction.ResetOpacity => availability.AtFullOpacity
            ? PinActionState.Blocked("当前已是不透明")
            : PinActionState.Always,
        _ => PinActionState.Always,
    };

    /// <summary>按钮是否处于激活态（14.3.6 的状态指示）：只有「切换」按钮有激活态，显示译文层时点亮。</summary>
    public static bool IsToggleOn(PinToolbarAction action, PinAvailability availability) =>
        action == PinToolbarAction.ToggleTextLayer && availability.OverlayShown;

    /// <summary>缩放倍数的显示文案（工具条上的倍数按钮；100% 表示原始尺寸）。</summary>
    public static string FormatZoom(double zoom)
    {
        var value = double.IsNaN(zoom) || zoom <= 0 ? 1.0 : zoom;
        return Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// 工具条的摆放（14.3.6「悬浮在图片下方 8 px、水平居中；**下方越界则移到图片内底部**」）：
    /// 本实现的窗口物理尺寸恒等于「图像像素 × 缩放」（14.3.7 的不变式），窗口外无处可画，
    /// 因此**始终走文档的「移到图片内底部」分支**；水平居中，窄窗口里按比例缩小；
    /// 缩小到 <see cref="MinScale"/> 以下、或图片本身矮到放不下缩小后的工具条时整体隐藏
    /// （压成迷你控件既看不清也点不准，而全部动作都有快捷键，不会因此失去能力）。
    /// </summary>
    public static PinToolbarPlacement ResolvePlacement(
        double windowWidth, double windowHeight, double toolbarWidth, double toolbarHeight, double margin)
    {
        if (windowWidth <= 0 || windowHeight <= 0 || toolbarWidth <= 0 || toolbarHeight <= 0 || margin < 0)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var available = windowWidth - (2 * margin);
        if (available <= 0)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var scale = Math.Min(1.0, available / toolbarWidth);

        // 高度也要放得下：钉图很小时（小选区 + 缩小）宁可不显示，也不让工具条压住整张图或溢出窗口被裁切
        if (scale < MinScale || (toolbarHeight * scale) + margin > windowHeight)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var width = toolbarWidth * scale;
        return new PinToolbarPlacement(Math.Max(margin, (windowWidth - width) / 2), scale, true);
    }

    /// <summary>
    /// **条带**摆放（v1.2 修复批 ④：工具条移到图片下方的常驻条带里，不再浮在图片上）：
    /// 只受宽度约束——条带高度是界面常量（不随缩放变化、也永不遮挡图片文字），高度不再参与隐藏判定；
    /// 窄窗口里按比例缩小，缩到 <see cref="MinScale"/> 以下时整体隐藏（全部动作仍有快捷键与右键菜单）。
    /// 返回的水平偏移供 Left 对齐布局使用；条带用居中对齐时可只取 <see cref="PinToolbarPlacement.Scale"/>
    /// 与 <see cref="PinToolbarPlacement.Visible"/>（偏移与居中结果一致）。
    /// </summary>
    public static PinToolbarPlacement ResolveBandPlacement(double windowWidth, double toolbarWidth, double margin)
    {
        if (windowWidth <= 0 || toolbarWidth <= 0 || margin < 0)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var available = windowWidth - (2 * margin);
        if (available <= 0)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var scale = Math.Min(1.0, available / toolbarWidth);
        if (scale < MinScale)
        {
            return new PinToolbarPlacement(0, 1, false);
        }

        var width = toolbarWidth * scale;
        return new PinToolbarPlacement(Math.Max(margin, (windowWidth - width) / 2), scale, true);
    }
}

/// <summary>工具条在窗口内的摆放结果：水平偏移（DIP）、缩放比例、是否可显示。</summary>
public readonly record struct PinToolbarPlacement(double OffsetX, double Scale, bool Visible);

/// <summary>钉图窗口里与 `<see cref="PinToolbarAction"/>` 对应的按键（Core 不引用 WPF，故自带一份键名）。</summary>
public enum PinKey
{
    /// <summary>本窗口不处理的键。</summary>
    None = 0,

    /// <summary>Esc。</summary>
    Escape,

    /// <summary>空格。</summary>
    Space,

    /// <summary>T。</summary>
    T,

    /// <summary>C。</summary>
    C,

    /// <summary>0。</summary>
    Digit0,

    /// <summary>加号（`=` 与 `+`、小键盘加号）。</summary>
    Plus,

    /// <summary>减号（`-`、小键盘减号）。</summary>
    Minus,

    /// <summary>R（重试翻译，Ctrl+R）。</summary>
    R,

    /// <summary>O（恢复不透明，Ctrl+O）。</summary>
    O,

    /// <summary>回车（在小窗中打开，Ctrl+Enter）。</summary>
    Enter,

    /// <summary>F（强制翻译，Ctrl+F；v1.2 修复批 ③-C：「识别语言 = 目标语言」跳过态的补救入口）。</summary>
    F,
}

/// <summary>
/// 钉图的键盘映射表（FR-027 / 14.3.6）：`Esc` 关闭、`空格`/`T` 切换原文译文、`Ctrl+C` 复制译文、
/// `Ctrl+0` 重置 100%、`Ctrl+=`/`Ctrl+-` 缩放、`Ctrl+R` 重试翻译、`Ctrl+Enter` 在小窗中打开、
/// `Ctrl+O` 恢复不透明、`Ctrl+F` 强制翻译（v1.2 修复批 ③-C）。
///
/// 文档只规定了前三项，其余是后续批次补的：**沿用浏览器 / 看图器的通用约定**
/// （`Ctrl+0` 回 100%、`Ctrl+加号/减号` 缩放、`Ctrl+R` 重试、`Ctrl+Enter` 送出/翻译），
/// `Ctrl+O` 则取「Opaque」之意（钉图窗口里没有「打开文件」语义，不会与常见习惯冲突），
/// 因此不需要用户学习，也**不与 `Esc` 冲突**（关闭动作只在无修饰键的 `Esc` 上，
/// 带 Ctrl 的 `Ctrl+Esc` 留给系统「开始菜单」）。
/// 带 Shift / Alt 的组合本批一律不占用（避免与输入法、系统组合键抢键）。
/// 「每个工具条动作都有键盘入口」是一条既有约束（批 4b 起由单测固定）：鼠标点得到，键盘也够得着。
/// </summary>
public static class PinShortcuts
{
    /// <summary>解析按键 → 动作；不认识的组合返回 <c>null</c>（调用方不处理、不吞键）。</summary>
    public static PinToolbarAction? Resolve(PinKey key, bool ctrl, bool shift, bool alt)
    {
        if (key == PinKey.None || shift || alt)
        {
            return null;
        }

        if (key == PinKey.Escape)
        {
            return ctrl ? null : PinToolbarAction.Close;
        }

        if (!ctrl)
        {
            return key switch
            {
                PinKey.Space or PinKey.T => PinToolbarAction.ToggleTextLayer,
                _ => null,
            };
        }

        return key switch
        {
            PinKey.C => PinToolbarAction.CopyTranslation,
            PinKey.Digit0 => PinToolbarAction.ResetZoom,
            PinKey.Plus => PinToolbarAction.ZoomIn,
            PinKey.Minus => PinToolbarAction.ZoomOut,
            PinKey.R => PinToolbarAction.RetryTranslation,
            PinKey.O => PinToolbarAction.ResetOpacity,
            PinKey.Enter => PinToolbarAction.OpenInQuickWindow,
            PinKey.F => PinToolbarAction.ForceTranslate,
            _ => null,
        };
    }
}
