namespace TranslationApp.Core.Placement;

/// <summary>虚拟桌面物理像素矩形（单位 px，坐标系为虚拟桌面物理坐标，副屏在主屏左侧时 Left 为负）。</summary>
public readonly record struct PhysicalRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 小窗物理像素定位算法（FR-025 / 14.1.3 / 14.1.4）：
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
    /// </summary>
    public static PhysicalRect Compute(
        int cursorX, int cursorY, PhysicalRect work, PhysicalRect windowPhys, int gapPhysical)
    {
        // 防御：即便调用方忘了先 FitToWorkArea，也不会摆出工作区（窗口比工作区大时退化为贴左上角）
        var width = work.Width > 0 ? Math.Min(windowPhys.Width, work.Width) : windowPhys.Width;
        var height = work.Height > 0 ? Math.Min(windowPhys.Height, work.Height) : windowPhys.Height;
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        var gap = Math.Max(gapPhysical, 0);

        var x = cursorX + gap;
        var y = cursorY + gap;

        if (x + width > work.Right)
        {
            x = cursorX - gap - width; // 右越界 → 翻到鼠标左侧
        }

        if (y + height > work.Bottom)
        {
            y = cursorY - gap - height; // 下越界 → 翻到鼠标上方
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        return new PhysicalRect(x, y, width, height);
    }
}
