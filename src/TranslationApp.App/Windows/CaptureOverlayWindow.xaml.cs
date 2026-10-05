using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Serilog;
using TranslationApp.Core.Capture;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>
/// 全屏遮罩框选窗口（FR-021 / 13.2.3）：
/// ① 只覆盖鼠标所在的**单个**显示器（跨屏框选是已声明限制：混合 DPI 下单个 WPF 窗口只有一个 DPI 比例）；
/// ② 不使用 WPF 的 Left/Top，改用 SetWindowPos 以**物理像素**摆放，并用 GetWindowRect 校验；
/// ③ 窗口内鼠标 DIP → 图像像素用**实测比值** scaleX = 物理宽 / ActualWidth 换算（13.2.3）。
/// 交互：拖拽框选 → 松开即确认；Esc / 右键取消；选区小于 4×4 DIP 视为误点按取消处理。
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    // 以下尺寸全部照设计稿 inkseal-ui.html 的 05 截图（.cap-* 一节）取值。
    private const double CornerSize = 16;      // 四角印角 16×16（.cap-corner）
    private const double CornerOverhang = 2;   // 印角向选区外溢 2px（left/right/top/bottom:-2px）
    private const double DiamondSize = 7;      // 边中点方菱形 7×7（.cap-dia）
    private const double LoupeWidth = 86;      // 放大镜宽 86（.cap-loupe width）
    private const double LoupeGap = 12;        // 放大镜与选区之间留 12px（.cap-loupe left:-98px）
    private const int LoupeCells = 5;          // 5×5 取色格
    private const int LoupeScale = 8;          // 每格代表 8×8 物理像素（读数 ×8）
    private const int LoupeCenter = 12;        // 5×5 的中心格（0 基，设计稿 .c）
    private const double ChipGap = 1;          // 尺寸牌相对选区右下角外溢 1px（.cap-size right/bottom:-1px）
    private const double HudMargin = 16;       // 顶部 HUD 距屏幕顶 16（.cap-hud top:16px）
    private const double HintMargin = 16;      // 底部提示距屏幕底 16（.cap-hint bottom:16px）

    private readonly int _maxImageDimension;
    private readonly NativeMethods.RECT _monitor;
    private readonly Rectangle[] _loupeCells = new Rectangle[LoupeCells * LoupeCells];
    private readonly SolidColorBrush[] _loupeBrushes = new SolidColorBrush[LoupeCells * LoupeCells];

    private byte[] _pixelBuffer = [];
    private Point _cursor;

    private Point? _dragStart;
    private Rect _selection;
    private bool _hasSelection;
    private bool _confirmed;
    private bool _dpiHopPending;

    internal CaptureOverlayWindow(
        BitmapSource screenshot,
        NativeMethods.RECT monitor,
        double scrimOpacity,
        int maxImageDimension)
    {
        InitializeComponent();

        _monitor = monitor;
        _maxImageDimension = maxImageDimension;

        Backdrop.Source = screenshot;
        ApplyScrimOpacity(scrimOpacity);
        CreateLoupeCells();

        Loaded += OnWindowLoaded;
        SizeChanged += OnWindowSizeChanged;
        UpdateScrimGeometry();
        UpdateHintChip();
    }

    /// <summary>实测缩放比 = 物理像素 / DIP（窗口布局后有效）。</summary>
    public double ScaleX { get; private set; } = 1.0;

    public double ScaleY { get; private set; } = 1.0;

    /// <summary>确认后的选区内 DIP 矩形（取消时为 null）。</summary>
    public DipRect? ConfirmedSelection { get; private set; }

    /// <summary>首次触达时替换顶部提示文案；确认后由调用方把提示状态落盘。</summary>
    internal void SetCaptureHint(string text)
    {
        HintText.Text = text;
        UpdateHintChip();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyPhysicalBounds();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // 13.2.3：PerMonitorV2 下触发 DPI 变化（窗口创建在非主屏）时重做摆放与缩放比重测
        if (_dpiHopPending)
        {
            return;
        }

        _dpiHopPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _dpiHopPending = false;
            ApplyPhysicalBounds();
            UpdateScaleFromActualSize();
            UpdateScrimGeometry();
            UpdateHintChip();
            UpdateCapHint();
            UpdateSelectionVisuals();
        }));
    }

    /// <summary>
    /// 以物理像素摆放窗口（13.2.3）：SetWindowPos(HWND_TOPMOST, rcMonitor)，
    /// 随后 GetWindowRect 校验，不一致时按差值纠正。
    /// </summary>
    private void ApplyPhysicalBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var width = _monitor.Right - _monitor.Left;
        var height = _monitor.Bottom - _monitor.Top;
        Place(hwnd, _monitor.Left, _monitor.Top, width, height);

        if (!ScreenInterop.GetWindowRect(hwnd, out var actual))
        {
            return;
        }

        if (actual.Left == _monitor.Left && actual.Top == _monitor.Top
            && actual.Right - actual.Left == width && actual.Bottom - actual.Top == height)
        {
            return;
        }

        Log.Debug(
            "遮罩窗口矩形与目标屏不一致（实际 {AL},{AT},{AW},{AH} / 期望 {EL},{ET},{EW},{EH}），按差值纠正",
            actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top,
            _monitor.Left, _monitor.Top, width, height);

        Place(
            hwnd,
            actual.Left + (_monitor.Left - actual.Left),
            actual.Top + (_monitor.Top - actual.Top),
            width,
            height);
    }

    private static void Place(IntPtr hwnd, int x, int y, int width, int height)
    {
        if (!ScreenInterop.SetWindowPos(
                hwnd, ScreenInterop.HWND_TOPMOST, x, y, width, height,
                ScreenInterop.SWP_SHOWWINDOW | ScreenInterop.SWP_NOACTIVATE))
        {
            Log.Warning("SetWindowPos 摆放遮罩窗口失败，Win32 错误码 {Error}", Marshal.GetLastWin32Error());
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPhysicalBounds();
        UpdateScaleFromActualSize();
        UpdateScrimGeometry();
        UpdateHintChip();
        UpdateCapHint();
        Keyboard.Focus(this);
        Log.Debug("遮罩窗口就绪：ActualSize={Width}x{Height}，Scale=({ScaleX},{ScaleY})",
            ActualWidth, ActualHeight, ScaleX, ScaleY);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScaleFromActualSize();
        UpdateScrimGeometry();
        UpdateHintChip();
        UpdateCapHint();
        UpdateSelectionVisuals();
    }

    private void UpdateScaleFromActualSize()
    {
        ScaleX = CaptureGeometry.ComputeScale(_monitor.Right - _monitor.Left, ActualWidth);
        ScaleY = CaptureGeometry.ComputeScale(_monitor.Bottom - _monitor.Top, ActualHeight);
    }

    /// <summary>遮罩底色的 alpha 取自 OcrScrimOpacity，RGB 仍来自固定令牌（13.7）。</summary>
    private void ApplyScrimOpacity(double opacity)
    {
        var baseColor = Application.Current?.TryFindResource("Color.Capture.Scrim") as Color? ?? Colors.Black;
        var alpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        Scrim.Fill = brush;
    }

    /// <summary>
    /// 放大镜的 5×5 取色格（设计稿 .cap-loupe .grid）：每格 0.5 DIP 外边距，
    /// 相邻两格之间自然让出 1px 缝（衬在 UniformGrid 的底色上）；中心格描一圈朱砂。
    /// </summary>
    private void CreateLoupeCells()
    {
        var baseColor = (Color)FindResource("Color.Capture.LoupeCell");
        for (var i = 0; i < _loupeCells.Length; i++)
        {
            var brush = new SolidColorBrush(baseColor);
            var cell = new Rectangle { Margin = new Thickness(0.5), Fill = brush };
            if (i == LoupeCenter)
            {
                cell.Stroke = (Brush)FindResource("Brush.Capture.Selection");
                cell.StrokeThickness = 1;
            }

            _loupeBrushes[i] = brush;
            _loupeCells[i] = cell;
            LoupeGrid.Children.Add(cell);
        }
    }

    // ==================== 鼠标交互（13.2.3 步骤 5/6）====================

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _hasSelection = false;
        _selection = Rect.Empty;
        CaptureMouse();
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        var current = e.GetPosition(this);
        _cursor = current;
        var normalized = CaptureGeometry.NormalizeDipRect(start.X, start.Y, current.X, current.Y);
        _selection = new Rect(normalized.X, normalized.Y, normalized.Width, normalized.Height);
        _hasSelection = true;
        UpdateSelectionVisuals();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        _dragStart = null;
        ReleaseMouseCapture();
        e.Handled = true;

        // 13.2.3 步骤 5/8：选区过小视为误点（按取消处理）；否则松开即确认并开始识别（AC 1）
        if (!_hasSelection || !CaptureGeometry.IsSelectionLargeEnough(_selection.Width, _selection.Height))
        {
            Log.Debug("选区过小（{Width}x{Height} DIP），按取消处理", _selection.Width, _selection.Height);
            Cancel();
            return;
        }

        Confirm();
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 备用确认路径（松开即确认已覆盖常规流程）
        if (_hasSelection)
        {
            Confirm();
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
            case Key.Enter or Key.Return:
                if (_hasSelection)
                {
                    e.Handled = true;
                    Confirm();
                }
                break;
        }
    }

    private void Confirm()
    {
        ConfirmedSelection = new DipRect(_selection.X, _selection.Y, _selection.Width, _selection.Height);
        _confirmed = true;
        DialogResult = true;
    }

    private void Cancel()
    {
        ConfirmedSelection = null;
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (!_confirmed)
        {
            ConfirmedSelection = null;
        }

        base.OnClosing(e);
    }

    // ==================== 视觉更新 ====================

    private void UpdateScrimGeometry()
    {
        var width = ActualWidth > 0 ? ActualWidth : _monitor.Right - _monitor.Left;
        var height = ActualHeight > 0 ? ActualHeight : _monitor.Bottom - _monitor.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var full = new RectangleGeometry(new Rect(0, 0, width, height));
        if (_hasSelection && !_selection.IsEmpty)
        {
            var combined = new CombinedGeometry(
                GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
            combined.Freeze();
            Scrim.Data = combined;
            return;
        }

        full.Freeze();
        Scrim.Data = full;
    }

    private void UpdateSelectionVisuals()
    {
        var visible = _hasSelection && !_selection.IsEmpty;
        var state = visible ? Visibility.Visible : Visibility.Collapsed;

        SelectionFill.Visibility = state;
        SelectionBorder.Visibility = state;
        SelectionInner.Visibility = state;
        SizeChip.Visibility = state;
        Loupe.Visibility = state;
        DiamondLeft.Visibility = state;
        DiamondRight.Visibility = state;
        GuideTop.Visibility = state;
        GuideBottom.Visibility = state;
        GuideLeft.Visibility = state;
        GuideRight.Visibility = state;
        CornerTL.Visibility = state;
        CornerTR.Visibility = state;
        CornerBL.Visibility = state;
        CornerBR.Visibility = state;

        if (!visible)
        {
            UpdateScrimGeometry();
            return;
        }

        var left = _selection.Left;
        var top = _selection.Top;
        var right = _selection.Right;
        var bottom = _selection.Bottom;
        var width = ActualWidth > 0 ? ActualWidth : _monitor.Right - _monitor.Left;
        var height = ActualHeight > 0 ? ActualHeight : _monitor.Bottom - _monitor.Top;

        // 选区本体（设计稿 .cap-sel）：淡朱砂填充 + 1px 朱砂描边 + 内侧 1px 白线（inset 阴影的等价画法）
        Place(SelectionFill, left, top, _selection.Width, _selection.Height);
        Place(SelectionBorder, left, top, _selection.Width, _selection.Height);
        Place(
            SelectionInner, left + 1, top + 1,
            Math.Max(0, _selection.Width - 2), Math.Max(0, _selection.Height - 2));

        // 四角印角（设计稿 .cap-corner）：16×16、向选区外溢 2px，各只画两条边
        PlaceAt(CornerTL, left - CornerOverhang, top - CornerOverhang);
        PlaceAt(CornerTR, right + CornerOverhang - CornerSize, top - CornerOverhang);
        PlaceAt(CornerBL, left - CornerOverhang, bottom + CornerOverhang - CornerSize);
        PlaceAt(CornerBR, right + CornerOverhang - CornerSize, bottom + CornerOverhang - CornerSize);

        // 左右边中点方菱形（设计稿 .cap-dia left/right:-4px）：中心正好落在描边上
        var diamondTop = top + (_selection.Height - DiamondSize) / 2;
        PlaceAt(DiamondLeft, left - DiamondSize / 2 - 0.5, diamondTop);
        PlaceAt(DiamondRight, right + 0.5 - DiamondSize / 2, diamondTop);

        // 贯穿辅助线（设计稿 .cap-guide）：选区的四条边一直拉到屏幕边缘
        GuideTop.X1 = 0;
        GuideTop.X2 = width;
        GuideTop.Y1 = GuideTop.Y2 = top + 0.5;
        GuideBottom.X1 = 0;
        GuideBottom.X2 = width;
        GuideBottom.Y1 = GuideBottom.Y2 = bottom + 0.5;
        GuideLeft.Y1 = 0;
        GuideLeft.Y2 = height;
        GuideLeft.X1 = GuideLeft.X2 = left + 0.5;
        GuideRight.Y1 = 0;
        GuideRight.Y2 = height;
        GuideRight.X1 = GuideRight.X2 = right + 0.5;

        UpdateSizeChip();
        UpdateLoupe(_cursor);
        UpdateScrimGeometry();
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    private static void PlaceAt(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    /// <summary>
    /// 尺寸牌（设计稿 .cap-size）：朱砂底白字，贴在选区右下角外侧；
    /// 文案是物理像素（用户关心截图分辨率），超过识别上限时追加缩放说明（13.2.4 / AC 7）。
    /// </summary>
    private void UpdateSizeChip()
    {
        var physicalWidth = (int)Math.Round(_selection.Width * ScaleX);
        var physicalHeight = (int)Math.Round(_selection.Height * ScaleY);
        var text = $"{physicalWidth} × {physicalHeight} · 已冻结";

        var fit = CaptureGeometry.FitToMaxDimension(physicalWidth, physicalHeight, _maxImageDimension);
        if (fit.Downscaled)
        {
            text += $"（已按 {fit.Ratio:0.##} 倍识别）";
        }

        SizeText.Text = text;
        SizeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chipWidth = SizeText.DesiredSize.Width + 16;
        var chipHeight = SizeText.DesiredSize.Height + 6;

        var width = ActualWidth > 0 ? ActualWidth : _monitor.Right - _monitor.Left;
        var height = ActualHeight > 0 ? ActualHeight : _monitor.Bottom - _monitor.Top;

        // 设计稿把牌子钉在选区右下角外侧（right/bottom:-1px + translateY(100%)）；越界时翻到选区上方
        var left = Math.Clamp(_selection.Right + ChipGap - chipWidth, 0, Math.Max(0, width - chipWidth));
        var top = _selection.Bottom + ChipGap;
        if (top + chipHeight > height)
        {
            top = Math.Max(0, _selection.Top - ChipGap - chipHeight);
        }

        PlaceAt(SizeChip, left, top);
    }

    /// <summary>
    /// 放大镜（设计稿 .cap-loupe）：挂在选区左侧 12px、顶端与选区齐平；
    /// 左侧放不下时翻到选区右侧。格子里是光标处真实像素（每格 8×8 物理像素取均值）。
    /// </summary>
    private void UpdateLoupe(Point dipPosition)
    {
        var width = ActualWidth > 0 ? ActualWidth : _monitor.Right - _monitor.Left;
        var height = ActualHeight > 0 ? ActualHeight : _monitor.Bottom - _monitor.Top;

        Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var loupeHeight = Loupe.DesiredSize.Height;

        var left = _selection.Left - LoupeWidth - LoupeGap;
        if (left < 0)
        {
            left = _selection.Right + LoupeGap;
        }

        PlaceAt(
            Loupe,
            Math.Clamp(left, 0, Math.Max(0, width - LoupeWidth)),
            Math.Clamp(_selection.Top, 0, Math.Max(0, height - loupeHeight)));

        SampleLoupe(dipPosition);
    }

    /// <summary>把光标处 5×5 个 8×8 物理像素块取均值填进格子，并刷新十六进制读数。</summary>
    private void SampleLoupe(Point dipPosition)
    {
        if (Backdrop.Source is not BitmapSource source || source.Format.BitsPerPixel != 32)
        {
            return;
        }

        var block = LoupeCells * LoupeScale;
        var offsetX = (int)Math.Round(dipPosition.X * ScaleX) - block / 2;
        var offsetY = (int)Math.Round(dipPosition.Y * ScaleY) - block / 2;
        var x0 = Math.Clamp(offsetX, 0, Math.Max(0, source.PixelWidth - block));
        var y0 = Math.Clamp(offsetY, 0, Math.Max(0, source.PixelHeight - block));
        var sampleWidth = Math.Min(block, source.PixelWidth - x0);
        var sampleHeight = Math.Min(block, source.PixelHeight - y0);
        if (sampleWidth <= 0 || sampleHeight <= 0)
        {
            return;
        }

        var stride = sampleWidth * 4;
        var length = stride * sampleHeight;
        if (_pixelBuffer.Length < length)
        {
            _pixelBuffer = new byte[length];
        }

        source.CopyPixels(new Int32Rect(x0, y0, sampleWidth, sampleHeight), _pixelBuffer, stride, 0);

        for (var row = 0; row < LoupeCells; row++)
        {
            for (var column = 0; column < LoupeCells; column++)
            {
                FillLoupeCell(
                    row * LoupeCells + column,
                    column * LoupeScale,
                    row * LoupeScale,
                    sampleWidth,
                    sampleHeight,
                    stride);
            }
        }

        var center = (sampleHeight / 2 * stride) + (sampleWidth / 2 * 4);
        LoupeHex.Text = $"#{_pixelBuffer[center + 2]:X2}{_pixelBuffer[center + 1]:X2}{_pixelBuffer[center]:X2}";
    }

    private void FillLoupeCell(int index, int x, int y, int width, int height, int stride)
    {
        var endX = Math.Min(x + LoupeScale, width);
        var endY = Math.Min(y + LoupeScale, height);
        if (x >= endX || y >= endY)
        {
            return;
        }

        var blue = 0;
        var green = 0;
        var red = 0;
        var count = 0;
        for (var row = y; row < endY; row++)
        {
            var offset = (row * stride) + (x * 4);
            for (var column = x; column < endX; column++)
            {
                blue += _pixelBuffer[offset];
                green += _pixelBuffer[offset + 1];
                red += _pixelBuffer[offset + 2];
                offset += 4;
                count++;
            }
        }

        _loupeBrushes[index].Color = Color.FromRgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    /// <summary>顶部 HUD（设计稿 .cap-hud）：水平居中，距屏幕顶 16px。</summary>
    private void UpdateHintChip()
    {
        HintChip.Visibility = Visibility.Visible;
        HintChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintChip, Math.Max(0, (ActualWidth - HintChip.DesiredSize.Width) / 2));
        Canvas.SetTop(HintChip, HudMargin);
    }

    /// <summary>底部提示（设计稿 .cap-hint）：水平居中，距屏幕底 16px。</summary>
    private void UpdateCapHint()
    {
        CapHint.Visibility = Visibility.Visible;
        CapHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(CapHint, Math.Max(0, (ActualWidth - CapHint.DesiredSize.Width) / 2));
        Canvas.SetTop(CapHint, Math.Max(0, ActualHeight - CapHint.DesiredSize.Height - HintMargin));
    }
}
