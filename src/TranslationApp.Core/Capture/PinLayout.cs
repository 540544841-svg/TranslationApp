using TranslationApp.Core.Placement;

namespace TranslationApp.Core.Capture;

/// <summary>钉图缩放范围（倍数，含两端）。</summary>
public readonly record struct PinZoomLimits(double Min, double Max);

/// <summary>
/// 覆盖层元素的几何（DIP，相对图片区域左上角）：由 <see cref="PinLayout.OverlayElement"/> 从覆盖块算出。
/// </summary>
/// <param name="Left">元素左边（DIP）。</param>
/// <param name="Top">元素顶边（DIP）。</param>
/// <param name="Width">元素宽（DIP，≥ 1）。</param>
/// <param name="Height">元素高（DIP，≥ 1）。</param>
/// <param name="FontSize">内容字号（DIP，≥ 1）。</param>
public readonly record struct PinOverlayElement(double Left, double Top, double Width, double Height, double FontSize);

/// <summary>钉图张数/像素总量上限的判定结果（FR-027 / 14.3.10）。</summary>
public enum PinBudgetKind
{
    /// <summary>允许钉图。</summary>
    Ok,

    /// <summary>张数已达 <c>PinMaxCount</c>。</summary>
    CountExceeded,

    /// <summary>加上新图后像素总量会超过 <c>PinMaxTotalPixels</c>。</summary>
    TotalExceeded,
}

/// <summary>
/// 钉图窗口的几何与上限判定（FR-027 / 14.3.6 / 14.3.7 / 14.3.10）：
/// 纯函数、无 WPF、无 P/Invoke，因此「多显示器 + 混合缩放」下的定位、缩放锚点与三重内存上限
/// 都能在单屏机器上穷举单测（与小窗的 <see cref="WindowPlacement"/> 同一范式）。
///
/// 全套实现只有一条不变式：**窗口物理尺寸 = 图像像素 × 缩放倍数**（zoom = 1.0 时就地逐像素还原原屏画面）；
/// DIP 尺寸只是它除以所在屏缩放的投影（<see cref="DipLength"/>），位置一律用物理像素。
/// </summary>
public static class PinLayout
{
    /// <summary>设置值缺失时的兜底缩放范围（14.6 默认 0.25 ~ 4.0）。</summary>
    public const double FallbackMin = 0.25;

    public const double FallbackMax = 4.0;

    /// <summary>
    /// Shift 精细步进相对倍率：<c>fine = 1 + (step − 1) × 0.2</c>
    /// （step 取默认 1.1 时恰为 14.3.6 的 ×1.02，用户改步进时精细步进按同一比例缩放，手感一致）。
    /// </summary>
    public const double FineStepRatio = 0.2;

    /// <summary>滚轮一格的标准增量（WHEEL_DELTA），用于折算一次事件里有几格。</summary>
    public const int WheelDelta = 120;

    /// <summary>滚轮步进的默认值（14.6 的 <c>PinZoomStep</c>）。</summary>
    public const double DefaultZoomStep = 1.1;

    /// <summary>
    /// 滚轮步进的可用范围（设置页据此夹取）：&lt; 1 会让滚轮方向失去意义（14.3.6 是等比放大），
    /// 过大则一格就跨完整段缩放范围；1.02 恰好是 Shift 精细步进的下限（1 + (1.02−1)×0.2）。
    /// </summary>
    public const double MinZoomStep = 1.02;

    public const double MaxZoomStep = 2.0;

    /// <summary>把设置里的滚轮步进夹取到可用范围（缺失/非法值回落 <see cref="DefaultZoomStep"/>）。</summary>
    public static double ClampZoomStep(double step) =>
        double.IsNaN(step) ? DefaultZoomStep : Math.Clamp(step, MinZoomStep, MaxZoomStep);

    // ==================== 透明度（FR-027 / 14.3.6） ====================

    /// <summary>透明度下限（14.3.6：再低就看不见内容了）。</summary>
    public const double MinOpacity = 0.3;

    /// <summary>透明度上限（不透明）。</summary>
    public const double MaxOpacity = 1.0;

    /// <summary>`Ctrl+滚轮` 每格的透明度步进（14.3.6）。</summary>
    public const double OpacityStep = 0.1;

    /// <summary>把透明度夹取到 <c>[0.3, 1.0]</c>（NaN 按不透明处理，绝不抛异常）。</summary>
    public static double ClampOpacity(double opacity) =>
        double.IsNaN(opacity) ? MaxOpacity : Math.Clamp(opacity, MinOpacity, MaxOpacity);

    /// <summary>
    /// `Ctrl+滚轮` 调透明度（14.3.6：步进 0.1，范围 `[0.3, 1.0]`）：向上更不透明、向下更透明；
    /// 结果按 3 位小数取整，避免浮点累积出 <c>0.7000000000000001</c> 这类值使「已是不透明」判不出来。
    /// 到达上下限即返回极限值，由调用方据「值未变」给出边界反馈（与缩放同一反馈路径）。
    /// </summary>
    public static double StepOpacity(double opacity, int wheelDelta)
    {
        var current = ClampOpacity(opacity);
        if (wheelDelta == 0)
        {
            return current;
        }

        var notches = Math.Max(1, Math.Abs(wheelDelta) / WheelDelta);
        var delta = OpacityStep * notches * (wheelDelta > 0 ? 1 : -1);
        return ClampOpacity(Math.Round(current + delta, 3));
    }

    /// <summary>透明度是否已到顶（右键菜单「恢复不透明」的启用依据）。</summary>
    public static bool IsFullyOpaque(double opacity) => opacity >= MaxOpacity - 0.0001;

    /// <summary>物理像素尺寸 = round(图像像素 × zoom)，两个方向都至少 1 px。</summary>
    public static (int Width, int Height) PhysicalSize(int imageWidth, int imageHeight, double zoom)
    {
        var value = SafeZoom(zoom);
        return (
            Math.Max(1, (int)Math.Round(imageWidth * value, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(imageHeight * value, MidpointRounding.AwayFromZero)));
    }

    /// <summary>物理长度 → DIP 长度（窗口所在屏的缩放，14.3.7 的尺寸同步规则）。</summary>
    public static double DipLength(int physicalLength, double dpiScale) =>
        physicalLength / (dpiScale > 0 ? dpiScale : 1.0);

    /// <summary>
    /// 文档 14.3.6 的单一换算系数 <c>k = 窗口 DIP 宽 / 图像像素宽</c>：
    /// 覆盖层内全部元素与字号都由它驱动，是「图片与译文永不错位」的唯一依据（批 4c 使用）。
    /// </summary>
    public static double ContentScale(int physicalWidth, double dpiScale, int imageWidth) =>
        imageWidth > 0 ? DipLength(physicalWidth, dpiScale) / imageWidth : 1.0;

    /// <summary>
    /// 含阴影外框的重载（v1.2 修复批 ④）：<c>k = 图片区 DIP 宽 / 图像像素宽</c>，
    /// 图片区 DIP 宽 = 窗口物理宽 − 2×frame 后再除以屏缩放。frame=0 时与三参数版逐位一致。
    /// 覆盖层坐标相对图片区左上角（与 <see cref="OverlayElement"/> 的输出一致），因此映射侧零改动。
    /// </summary>
    public static double ContentScale(int physicalWidth, double dpiScale, int imageWidth, int framePhysical)
    {
        if (imageWidth <= 0)
        {
            return 1.0;
        }

        var frame = Math.Max(0, framePhysical);
        return DipLength(Math.Max(1, physicalWidth - (2 * frame)), dpiScale) / imageWidth;
    }

    /// <summary>
    /// 覆盖层元素的几何（DIP，相对图片区域左上角）：块的图像像素矩形与字号一律乘同一系数 k，
    /// 因此缩放时覆盖块与图片**永不错位**（14.3.6 的单一换算系数）。
    /// 纯逻辑放在这里，界面只负责把结果套到 WPF 元素上（这样「块 → 元素矩形与字号」可单测）。
    /// </summary>
    /// <param name="block">覆盖块（图像像素坐标）。</param>
    /// <param name="k">换算系数（= <see cref="ContentScale"/>）。k ≤ 0 时返回全 0（界面据此跳过绘制）。</param>
    public static PinOverlayElement OverlayElement(in PinOverlayBlock block, double k)
    {
        if (double.IsNaN(k) || k <= 0)
        {
            return default;
        }

        var height = Math.Max(1, block.Rect.Height * k);
        // 字号缺失（≤0）时按块高推算（与既有实现一致）；下限 1 px：绝不生成 0 号字（0 号字不可见且会触发 WPF 参数异常）
        var fontPx = block.FontSizePx > 0 ? block.FontSizePx : block.Rect.Height * 0.42;
        return new PinOverlayElement(
            block.Rect.X * k,
            block.Rect.Y * k,
            Math.Max(1, block.Rect.Width * k),
            height,
            Math.Max(1, fontPx * k));
    }

    /// <summary>把缩放倍数夹取到允许范围（范围本身非法时退化到兜底值，绝不抛异常）。</summary>
    public static double ClampZoom(double zoom, PinZoomLimits limits)
    {
        var min = limits.Min > 0 ? limits.Min : FallbackMin;
        var max = limits.Max >= min ? limits.Max : min;
        return Math.Clamp(SafeZoom(zoom), min, max);
    }

    /// <summary>
    /// 解析实际生效的缩放范围（14.3.6）：上限 = <c>min(设置上限, 工作区宽/图像宽, 工作区高/图像高)</c>
    /// —— 上限受所在工作区约束，保证钉图**始终完整可见**（不出现「内容跑到屏幕外找不回来」）。
    /// 工作区小到连设置下限都放不下时，下限让位（完整可见优先于最小倍数），保证 Min ≤ Max 且 Max 恒可完整显示。
    /// </summary>
    public static PinZoomLimits ResolveLimits(
        double settingMin, double settingMax, PhysicalRect work, int imageWidth, int imageHeight)
    {
        var min = settingMin > 0 ? settingMin : FallbackMin;
        var max = settingMax > 0 ? settingMax : FallbackMax;
        if (max < min)
        {
            max = min;
        }

        if (work.Width > 0 && work.Height > 0 && imageWidth > 0 && imageHeight > 0)
        {
            var fit = Math.Min((double)work.Width / imageWidth, (double)work.Height / imageHeight);
            if (fit > 0)
            {
                max = Math.Min(max, fit);
                min = Math.Min(min, max);
            }
        }

        return new PinZoomLimits(min, max);
    }

    /// <summary>
    /// 滚轮步进（14.3.6：等比，连续滚动手感线性）：向上放大、向下缩小；
    /// <paramref name="fine"/>（Shift）用 <see cref="FineStepRatio"/> 折算的精细倍率；
    /// 返回值已按 <paramref name="limits"/> 夹取（到达极限即等于极限值，由调用方据"值未变"给出边界反馈）。
    /// </summary>
    public static double StepZoom(double zoom, int wheelDelta, double step, bool fine, PinZoomLimits limits)
    {
        var current = ClampZoom(zoom, limits);
        if (wheelDelta == 0)
        {
            return current;
        }

        var baseStep = step > 1.0 ? step : 1.1;
        var factor = fine ? 1.0 + (baseStep - 1.0) * FineStepRatio : baseStep;
        var notches = Math.Max(1, Math.Abs(wheelDelta) / WheelDelta);
        var target = wheelDelta > 0
            ? current * Math.Pow(factor, notches)
            : current / Math.Pow(factor, notches);

        return ClampZoom(target, limits);
    }

    /// <summary>
    /// 把矩形收进工作区：先按工作区收缩尺寸（完整可见优先），再钳制位置。
    /// 工作区本身不跨屏，因此「钳制」等价于「不越界到相邻显示器」。
    /// </summary>
    public static PhysicalRect ClampIntoWork(PhysicalRect rect, PhysicalRect work)
    {
        if (work.Width <= 0 || work.Height <= 0)
        {
            return rect;
        }

        var width = Math.Clamp(rect.Width, 1, work.Width);
        var height = Math.Clamp(rect.Height, 1, work.Height);
        return new PhysicalRect(
            Math.Clamp(rect.Left, work.Left, work.Right - width),
            Math.Clamp(rect.Top, work.Top, work.Bottom - height),
            width,
            height);
    }

    /// <summary>
    /// 初始物理矩形（14.3.7）：**贴在原选区位置**（钉在它被框选的地方，而不是鼠标处），
    /// 尺寸 = 图像像素 × zoom，最后按工作区钳制/收缩（选区比工作区大时同样保证完整可见）。
    /// </summary>
    /// <param name="originX">选区左上角的屏幕物理 X（= 显示器物理 Left + 选区图像 X）。</param>
    public static PhysicalRect InitialRect(
        int originX, int originY, int imageWidth, int imageHeight, PhysicalRect work, double zoom)
    {
        var (width, height) = PhysicalSize(imageWidth, imageHeight, zoom);
        return ClampIntoWork(new PhysicalRect(originX, originY, width, height), work);
    }

    /// <summary>
    /// 以鼠标为锚点缩放（14.3.7 锚点数学）：<paramref name="fractionX"/>/<paramref name="fractionY"/> 是鼠标在
    /// 窗口内的分数位置（0~1，取 DIP 比值，与 DPI 无关），缩放后该分数位置对应的屏幕物理点保持不动。
    /// 最后钳制进工作区——钳到边缘时锚点会轻微偏移，这是「保证完整可见」的必然代价。
    /// </summary>
    public static PhysicalRect ZoomAt(
        PhysicalRect current, double fractionX, double fractionY,
        double newZoom, int imageWidth, int imageHeight, PhysicalRect work)
    {
        var fx = Math.Clamp(double.IsNaN(fractionX) ? 0.5 : fractionX, 0, 1);
        var fy = Math.Clamp(double.IsNaN(fractionY) ? 0.5 : fractionY, 0, 1);
        var (width, height) = PhysicalSize(imageWidth, imageHeight, newZoom);

        var anchorX = current.Left + fx * Math.Max(current.Width, 1);
        var anchorY = current.Top + fy * Math.Max(current.Height, 1);

        return ClampIntoWork(
            new PhysicalRect(
                (int)Math.Round(anchorX - fx * width, MidpointRounding.AwayFromZero),
                (int)Math.Round(anchorY - fy * height, MidpointRounding.AwayFromZero),
                width,
                height),
            work);
    }

    /// <summary>
    /// 单张钉图像素上限的降采样决策（14.3.10：超过 <c>PinMaxPixelsPerImage</c> 时**等比缩小后钉图并提示**，
    /// 不拒绝用户）。返回形状与 <see cref="CaptureGeometry.FitToMaxDimension"/> 一致，便于复用 <see cref="BgraImage.Resize"/>；
    /// <c>Downscaled = false</c> 时 Ratio 恒为 1。
    /// </summary>
    public static (int Width, int Height, double Ratio, bool Downscaled) FitToPixelLimit(
        int width, int height, long maxPixels)
    {
        if (width <= 0 || height <= 0 || maxPixels <= 0)
        {
            return (Math.Max(width, 0), Math.Max(height, 0), 1.0, false);
        }

        var pixels = (long)width * height;
        if (pixels <= maxPixels)
        {
            return (width, height, 1.0, false);
        }

        var ratio = Math.Sqrt((double)maxPixels / pixels);
        var newWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var newHeight = Math.Max(1, (int)Math.Round(height * ratio));

        // 取整可能让面积仍略超上限：按长边先收，直到落入上限（最多几十次，开销可忽略）
        while ((long)newWidth * newHeight > maxPixels && (newWidth > 1 || newHeight > 1))
        {
            if (newWidth >= newHeight && newWidth > 1)
            {
                newWidth--;
            }
            else
            {
                newHeight--;
            }
        }

        return (newWidth, newHeight, (double)newWidth / width, true);
    }

    /// <summary>
    /// 段数与像素上限判定（14.3.10）：任一达到即拒绝新钉图并提示，
    /// **绝不静默关闭已有钉图**（静默丢弃用户内容不可接受）。
    /// </summary>
    /// <param name="newPixels">新钉图的像素数（已按单张上限降采样后的值）。</param>
    public static PinBudgetKind CheckBudget(
        int existingCount, long existingPixels, long newPixels, int maxCount, long maxTotalPixels)
    {
        if (maxCount > 0 && existingCount >= maxCount)
        {
            return PinBudgetKind.CountExceeded;
        }

        if (maxTotalPixels > 0 && existingPixels + Math.Max(newPixels, 0) > maxTotalPixels)
        {
            return PinBudgetKind.TotalExceeded;
        }

        return PinBudgetKind.Ok;
    }

    // ==================== 模式 C（下方译文面板）的尺寸扩展（FR-027 / 14.3.5 / 14.3.7） ====================
    //
    // 不变式扩展：窗口物理尺寸 = 图像像素 × zoom **+ 面板物理高度**（面板物理高 = 面板 DIP × 所在屏缩放，
    // 与 zoom 无关：面板是界面元素，文字必须始终可读，不随图片缩放）。
    // 面板高度为 0 时下列函数与不带面板的同名函数**逐位等价**，因此批 4a/4b 的行为不受影响。

    /// <summary>含面板的窗口物理尺寸（面板高度为 0 时等于 <see cref="PhysicalSize"/>）。</summary>
    public static (int Width, int Height) PhysicalSizeWithPanel(
        int imageWidth, int imageHeight, double zoom, int panelPhysicalHeight)
    {
        var (width, height) = PhysicalSize(imageWidth, imageHeight, zoom);
        return (width, height + Math.Max(0, panelPhysicalHeight));
    }

    /// <summary>面板占用高度后的工作区（缩放上限据此计算，保证图片与面板整体完整可见）。</summary>
    public static PhysicalRect WorkWithoutPanel(PhysicalRect work, int panelPhysicalHeight)
    {
        var panel = Math.Max(0, panelPhysicalHeight);
        return panel == 0
            ? work
            : new PhysicalRect(work.Left, work.Top, work.Width, Math.Max(1, work.Height - panel));
    }

    /// <summary>
    /// 含面板的初始物理矩形：位置不变，高度加上面板；仍按工作区钳制（选区比工作区大时完整可见优先）。
    /// </summary>
    public static PhysicalRect InitialRectWithPanel(
        int originX, int originY, int imageWidth, int imageHeight,
        PhysicalRect work, double zoom, int panelPhysicalHeight)
    {
        var (width, height) = PhysicalSizeWithPanel(imageWidth, imageHeight, zoom, panelPhysicalHeight);
        return ClampIntoWork(new PhysicalRect(originX, originY, width, height), work);
    }

    /// <summary>
    /// 含面板的鼠标锚点缩放：<paramref name="fractionY"/> 相对**图片区域**（不含面板）的分数位置，
    /// 缩放后该点保持不动；面板高度是常量，因此整体仍是仿射映射，锚点数学与无面板时同构。
    /// </summary>
    public static PhysicalRect ZoomAtWithPanel(
        PhysicalRect current, double fractionX, double fractionY,
        double newZoom, int imageWidth, int imageHeight,
        int panelPhysicalHeight, PhysicalRect work)
    {
        var panel = Math.Max(0, panelPhysicalHeight);
        if (panel == 0)
        {
            return ZoomAt(current, fractionX, fractionY, newZoom, imageWidth, imageHeight, work);
        }

        var fx = Math.Clamp(double.IsNaN(fractionX) ? 0.5 : fractionX, 0, 1);
        var fy = Math.Clamp(double.IsNaN(fractionY) ? 0.5 : fractionY, 0, 1);
        var (width, height) = PhysicalSizeWithPanel(imageWidth, imageHeight, newZoom, panel);
        var imageAreaHeight = Math.Max(1, current.Height - panel);

        var anchorX = current.Left + (fx * Math.Max(current.Width, 1));
        var anchorY = current.Top + (fy * imageAreaHeight);

        return ClampIntoWork(
            new PhysicalRect(
                (int)Math.Round(anchorX - (fx * width), MidpointRounding.AwayFromZero),
                (int)Math.Round(anchorY - (fy * (height - panel)), MidpointRounding.AwayFromZero),
                width,
                height),
            work);
    }

    // ==================== 阴影外框 + 工具条条带（chrome）的尺寸扩展（v1.2 修复批 ④ / ②） ====================
    //
    // 不变式改写：窗口物理尺寸 = 图像像素 × zoom + 2×framePhys + chromePhys。
    // frame = 四周阴影边距（宽高各加两倍；窗口左上 = 选区原点 − frame，图片仍对齐原选区）；
    // chrome = 底部「工具条条带 + 译文面板」的物理高度（只加在高度上）。
    // 两者都是界面元素，按 round(DIP × 屏缩放) 折算，**不随 zoom 变化**（界面文字始终可读）。
    // frame = 0 且 chrome = 面板高度时，下列函数与上面的 *WithPanel 函数**逐位一致**（旧函数保留给既有测试）。

    /// <summary>含阴影外框与 chrome 的窗口物理尺寸（frame=0 且 chrome=panel 时等于 <see cref="PhysicalSizeWithPanel"/>）。</summary>
    public static (int Width, int Height) PhysicalSizeWithChrome(
        int imageWidth, int imageHeight, double zoom, int framePhysical, int chromePhysical)
    {
        var frame = Math.Max(0, framePhysical);
        var (width, height) = PhysicalSize(imageWidth, imageHeight, zoom);
        return (width + (2 * frame), height + (2 * frame) + Math.Max(0, chromePhysical));
    }

    /// <summary>阴影外框与 chrome 占用后的工作区（缩放上限据此计算，保证「图片 + 条带 (+面板)」整体完整可见）。</summary>
    public static PhysicalRect WorkWithoutChrome(PhysicalRect work, int framePhysical, int chromePhysical)
    {
        var frame = Math.Max(0, framePhysical);
        var chrome = Math.Max(0, chromePhysical);
        if (frame == 0)
        {
            return WorkWithoutPanel(work, chrome);
        }

        return new PhysicalRect(
            work.Left + frame,
            work.Top + frame,
            Math.Max(1, work.Width - (2 * frame)),
            Math.Max(1, work.Height - (2 * frame) - chrome));
    }

    /// <summary>
    /// 含阴影外框与 chrome 的初始物理矩形：**窗口左上 = 选区原点 − frame**（图片仍钉在原选区位置），
    /// 再按工作区钳制（选区比工作区大时完整可见优先）。
    /// </summary>
    public static PhysicalRect InitialRectWithChrome(
        int originX, int originY, int imageWidth, int imageHeight,
        PhysicalRect work, double zoom, int framePhysical, int chromePhysical)
    {
        var frame = Math.Max(0, framePhysical);
        var (width, height) = PhysicalSizeWithChrome(imageWidth, imageHeight, zoom, frame, chromePhysical);
        return ClampIntoWork(
            new PhysicalRect(originX - frame, originY - frame, width, height), work);
    }

    /// <summary>
    /// 含阴影外框与 chrome 的鼠标锚点缩放：<paramref name="fractionX"/>/<paramref name="fractionY"/>
    /// 相对**图片区矩形**（扣除四周阴影边距与底部 chrome）的分数位置，缩放后该点保持不动；
    /// frame/chrome 都是常量，因此整体仍是仿射映射，锚点数学与无 chrome 时同构。
    /// </summary>
    public static PhysicalRect ZoomAtWithChrome(
        PhysicalRect current, double fractionX, double fractionY,
        double newZoom, int imageWidth, int imageHeight,
        int framePhysical, int chromePhysical, PhysicalRect work)
    {
        var frame = Math.Max(0, framePhysical);
        var chrome = Math.Max(0, chromePhysical);
        if (frame == 0)
        {
            return ZoomAtWithPanel(current, fractionX, fractionY, newZoom, imageWidth, imageHeight, chrome, work);
        }

        var fx = Math.Clamp(double.IsNaN(fractionX) ? 0.5 : fractionX, 0, 1);
        var fy = Math.Clamp(double.IsNaN(fractionY) ? 0.5 : fractionY, 0, 1);
        var (width, height) = PhysicalSizeWithChrome(imageWidth, imageHeight, newZoom, frame, chrome);
        var imageAreaWidth = Math.Max(1, current.Width - (2 * frame));
        var imageAreaHeight = Math.Max(1, current.Height - (2 * frame) - chrome);

        var anchorX = current.Left + frame + (fx * imageAreaWidth);
        var anchorY = current.Top + frame + (fy * imageAreaHeight);

        return ClampIntoWork(
            new PhysicalRect(
                (int)Math.Round(anchorX - frame - (fx * (width - (2 * frame))), MidpointRounding.AwayFromZero),
                (int)Math.Round(anchorY - frame - (fy * (height - (2 * frame) - chrome)), MidpointRounding.AwayFromZero),
                width,
                height),
            work);
    }

    private static double SafeZoom(double zoom) => double.IsNaN(zoom) || zoom <= 0 ? 1.0 : zoom;
}
