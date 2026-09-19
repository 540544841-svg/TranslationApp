namespace TranslationApp.Core.Capture;

/// <summary>图像像素矩形（相对图像左上角，单位 px）。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>窗口内 DIP 矩形（相对遮罩窗口左上角）。</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// 截图几何换算（13.2.2 要求纯计算放 Core，可单元测试）：
/// 物理像素 ↔ DIP、DIP 选区 → 图像像素裁剪矩形、超限等比缩小决策。
///
/// 为什么用「实测比值」而不是 GetDpiForMonitor 推算（13.2.3）：
/// 遮罩窗口以物理像素摆放后，WPF 的 ActualWidth 是系统在该 DPI 下换算出的 DIP 宽度，
/// 二者相除得到的 scaleX 已自动吸收四舍五入与 PerMonitorV2 的差异，避免混合 DPI 错位。
/// </summary>
public static class CaptureGeometry
{
    /// <summary>有效选区最小边长（DIP）：更小视为误点按取消处理（13.2.3 步骤 5）。</summary>
    public const double MinSelectionDip = 4.0;

    /// <summary>窗口/图像长度太小时比值不可信，退化为 1:1。</summary>
    private const double MinUsableLength = 1.0;

    /// <summary>实测缩放比 = 物理像素长度 / DIP 长度。</summary>
    public static double ComputeScale(double physicalLength, double dipLength) =>
        physicalLength >= MinUsableLength && dipLength >= MinUsableLength
            ? physicalLength / dipLength
            : 1.0;

    /// <summary>选区是否足够大（两个方向都要 ≥ 4 DIP）。</summary>
    public static bool IsSelectionLargeEnough(double dipWidth, double dipHeight) =>
        dipWidth >= MinSelectionDip && dipHeight >= MinSelectionDip;

    /// <summary>把鼠标按下/抬起的两个 DIP 点归一化成正向矩形（支持任意方向拖拽）。</summary>
    public static DipRect NormalizeDipRect(double x1, double y1, double x2, double y2) =>
        new(
            Math.Min(x1, x2),
            Math.Min(y1, y2),
            Math.Abs(x2 - x1),
            Math.Abs(y2 - y1));

    /// <summary>
    /// DIP 选区 → 图像像素矩形（13.2.3）：按实测比值换算后 Clamp 到图像边界内。
    /// 宽高至少 1 像素，保证后续裁剪不退化成空图。
    /// </summary>
    public static PixelRect DipToImageRect(
        DipRect selection, double scaleX, double scaleY, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        var left = (int)Math.Round(selection.X * scaleX);
        var top = (int)Math.Round(selection.Y * scaleY);
        var width = (int)Math.Round(selection.Width * scaleX);
        var height = (int)Math.Round(selection.Height * scaleY);

        left = Math.Clamp(left, 0, imageWidth - 1);
        top = Math.Clamp(top, 0, imageHeight - 1);
        width = Math.Clamp(width, 1, imageWidth - left);
        height = Math.Clamp(height, 1, imageHeight - top);

        return new PixelRect(left, top, width, height);
    }

    /// <summary>
    /// 超长边等比缩小到 OcrEngine.MaxImageDimension 以内（13.2.3 步骤 7）。
    /// maxDimension 由调用方运行期读取并传入，绝不硬编码；
    /// Downscaled=false 时 Ratio 恒为 1，界面据此决定是否显示「已按 x 倍识别」。
    /// </summary>
    public static (int Width, int Height, double Ratio, bool Downscaled) FitToMaxDimension(
        int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0 || maxDimension <= 0)
        {
            return (Math.Max(width, 0), Math.Max(height, 0), 1.0, false);
        }

        var longest = Math.Max(width, height);
        if (longest <= maxDimension)
        {
            return (width, height, 1.0, false);
        }

        var ratio = (double)maxDimension / longest;
        var newWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var newHeight = Math.Max(1, (int)Math.Round(height * ratio));
        return (newWidth, newHeight, ratio, true);
    }
}
