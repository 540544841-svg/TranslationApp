namespace TranslationApp.Core.Placement;

/// <summary>虚拟桌面物理像素矩形（单位 px，坐标系为虚拟桌面物理坐标，副屏在主屏左侧时 Left 为负）。</summary>
public readonly record struct PhysicalRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 小窗物理像素定位算法（FR-025 / 14.1.3 / 14.1.4）：
/// <summary>
/// 本次呼出选定的贴边方向（14.1.3 的越界翻转只在首次摆放判一次）：
/// Horizontal = 1 光标右侧 / -1 光标左侧；Vertical = 1 光标下方 / -1 光标上方。
/// 之所以要由调用方固定下来：译文落定后窗口会长高长宽，若每次都按新尺寸重判，
/// 一旦越过工作区下沿/右沿就会当场翻到另一侧——用户看到的是整窗跳走。
/// </summary>
public readonly record struct PlacementSide(int Horizontal, int Vertical)
{
    /// <summary>未决定：交给 <see cref="WindowPlacement.Compute"/> 按当前尺寸自动翻转（只在首次摆放用）。</summary>
    public static PlacementSide Auto => default;

    /// <summary>是否尚未决定贴边方向。</summary>
    public bool IsAuto => Horizontal == 0 && Vertical == 0;
}

/// 纯函数、无 P/Invoke、无 WPF 依赖，因此「多显示器 + 混合缩放」的全部几何决策都能在单屏机器上穷举单测。
///
/// 为什么必须用物理像素：WPF 的 <c>Window.Left/Top/Width/Height</c> 都是 DIP，
/// WPF 会按窗口**当前所在显示器**的缩放把它换算成物理像素；当鼠标所在屏与窗口所在屏缩放不同时，
/// 「把物理坐标除以目标屏缩放当 DIP 塞给 WPF」必然错位（这正是 FR-025 的根因）。
/// 本算法只做「物理坐标进 → 物理矩形出」，由 App 层用 SetWindowPos 直接落地。
/// </summary>
public static class WindowPlacement
{
    /// <summary>鼠标旁偏移（DIP，FR-002 的「约 16px 偏移」；物理间距 = round(Gap × scale)）。</summary>
    public const int CursorGapDip = 16;

    /// <summary>XAML 外层阴影留白（DIP）：QuickWindow 根 Border 的 Margin，定位时计入窗口物理尺寸。</summary>
    public const int ShadowMarginDip = 12;

    /// <summary>DIP → 物理像素长度（四舍五入，away-from-zero 保证结果确定、可复现）。</summary>
    public static int ToPhysicalLength(double dip, double scale) =>
        (int)Math.Round(dip * (scale > 0 ? scale : 1.0), MidpointRounding.AwayFromZero);

    /// <summary>
    /// 工作区尺寸不足时把窗口收缩到工作区大小（14.1.3「窗口比工作区还大」规则）：
    /// 返回的物理尺寸**永不大于工作区**（这是「绝不跨越到相邻屏」的关键补充），
    /// 同时不低于 minWidth/minHeight（最小值本身大于工作区时以工作区为准，保证完整可见）。
    /// minWidth/minHeight 单位为**物理像素**，由 App 用 MinWidth/MinHeight(DIP) × scale 换算后传入。
    /// </summary>
    public static PhysicalRect FitToWorkArea(PhysicalRect window, PhysicalRect work, int minWidth, int minHeight)
    {
        if (work.Width <= 0 || work.Height <= 0)
        {
            return window; // 退化工作区：不做收缩，交由调用方处理
        }

        var lowerWidth = Math.Clamp(minWidth, 1, work.Width);
        var lowerHeight = Math.Clamp(minHeight, 1, work.Height);

        return window with
        {
            Width = Math.Clamp(window.Width, lowerWidth, work.Width),
            Height = Math.Clamp(window.Height, lowerHeight, work.Height),
        };
    }

    /// <summary>
    /// 物理像素定位（14.1.3 规则表）：以鼠标所在屏的**工作区**为基准，右下偏移优先 →
    /// 右/下越界翻转 → 仍越界则钳制到工作区内（钳制即等价于「不跨屏」，工作区本身不跨屏）。
    /// 返回值为最终应交给 <c>SetWindowPos</c> 的物理矩形（尺寸亦为最终值）。
    /// <paramref name="side"/> 非 Auto 时按指定方向摆、不再自行翻边——同一呼出会话内必须沿用
    /// 首次定下的方向（见 <see cref="PlacementSide"/>），否则译文到达时窗口会当场翻到另一侧。
    /// </summary>
    public static PhysicalRect Compute(
        int cursorX, int cursorY, PhysicalRect work, PhysicalRect windowPhys, int gapPhysical,
        PlacementSide side = default)
    {
        // 防御：即便调用方忘了先 FitToWorkArea，也不会摆出工作区（窗口比工作区大时退化为贴左上角）
        var (width, height) = NormalizeSize(work, windowPhys);
        var gap = Math.Max(gapPhysical, 0);

        var resolved = side.IsAuto ? DecideSide(cursorX, cursorY, work, width, height, gap) : side;

        var x = resolved.Horizontal > 0 ? cursorX + gap : cursorX - gap - width;
        var y = resolved.Vertical > 0 ? cursorY + gap : cursorY - gap - height;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        return new PhysicalRect(x, y, width, height);
    }

    /// <summary>
    /// 锁住上边沿（14.1.3 补充，「顶边不跳」的几何保证）：把已算好的矩形顶边固定在
    /// <paramref name="pinnedTop"/>，其余几何（左 / 宽 / 高）原样保留。
    ///
    /// 为什么需要它：<see cref="Compute"/> 会在「窗口放不下」时把 y 往上钳制，于是译文越长、
    /// 顶边被顶得越高——用户看到的就是「拉长跳变」。锁顶边后只向下生长，视觉上窗口是稳稳长出来的。
    /// 只有一种例外必须让步：<c>顶边 + 高度</c>越出工作区下沿（或顶边本身在工作区外）时按工作区钳制，
    /// 保证窗口始终完整可见——调用方通过限制目标高度（见 <see cref="AvailableHeightBelow"/>）避免走到这一步。
    /// </summary>
    public static PhysicalRect PinTopEdge(PhysicalRect rect, PhysicalRect work, int pinnedTop)
    {
        if (work.Height <= 0)
        {
            return rect with { Top = pinnedTop }; // 退化工作区：只锁顶边，不做可见性钳制
        }

        // 用 Min/Max 而非 Clamp：窗口比工作区还高时 Clamp 的 min > max 会抛异常
        var upper = Math.Max(work.Top, work.Bottom - rect.Height);
        var top = Math.Min(Math.Max(pinnedTop, work.Top), upper);
        return rect with { Top = top };
    }

    /// <summary>
    /// 锁住顶边后「顶边到工作区下沿」还能容纳的物理高度：自适应限高的输入，
    /// 保证目标高度永远放得下，不会触发 <see cref="PinTopEdge"/> 的钳制（即不会把顶边顶上去）。
    /// <paramref name="pinnedTop"/> 为空（顶边尚未落地）时退化为整个工作区高度。
    /// </summary>
    public static int AvailableHeightBelow(int? pinnedTop, PhysicalRect work) =>
        pinnedTop is { } top ? Math.Max(0, work.Bottom - top) : Math.Max(0, work.Height);

    /// <summary>
    /// 光标所在贴边方向上「光标到工作区边沿」还能容纳的物理宽度（已扣除光标偏移）：
    /// 自适应限宽的输入，避免加宽到越界、再被 <see cref="Compute"/> 向左钳制造成横向跳变。
    /// 方向未定（<see cref="PlacementSide.Auto"/>）时按右侧计算。
    /// </summary>
    public static int AvailableWidthOnSide(
        PlacementSide side, int cursorX, PhysicalRect work, int gapPhysical)
    {
        var gap = Math.Max(gapPhysical, 0);
        return side.Horizontal < 0
            ? Math.Max(0, cursorX - gap - work.Left)
            : Math.Max(0, work.Right - (cursorX + gap));
    }

    /// <summary>
    /// 按当前尺寸判断本次该贴哪一侧——与 <see cref="Compute"/> 的 Auto 行为是同一套判定。
    /// 调用方在本次呼出的首次摆放取一次并固定下来，之后尺寸变化只钳制、不翻边。
    /// </summary>
    public static PlacementSide DecideSide(
        int cursorX, int cursorY, PhysicalRect work, PhysicalRect windowPhys, int gapPhysical)
    {
        var (width, height) = NormalizeSize(work, windowPhys);
        return DecideSide(cursorX, cursorY, work, width, height, Math.Max(gapPhysical, 0));
    }

    private static PlacementSide DecideSide(
        int cursorX, int cursorY, PhysicalRect work, int width, int height, int gap) =>
        new(cursorX + gap + width > work.Right ? -1 : 1,
            cursorY + gap + height > work.Bottom ? -1 : 1);

    /// <summary>尺寸归一：即便调用方忘了先 FitToWorkArea，也不会摆出工作区（窗口比工作区大时退化为贴左上角）。</summary>
    private static (int Width, int Height) NormalizeSize(PhysicalRect work, PhysicalRect windowPhys)
    {
        var width = work.Width > 0 ? Math.Min(windowPhys.Width, work.Width) : windowPhys.Width;
        var height = work.Height > 0 ? Math.Min(windowPhys.Height, work.Height) : windowPhys.Height;
        return (Math.Max(width, 1), Math.Max(height, 1));
    }
}
