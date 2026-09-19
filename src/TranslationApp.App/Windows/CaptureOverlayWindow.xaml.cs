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
    private const double HandleSize = 6;    // 8 个手柄 6×6 DIP（13.2.4）
    private const double SelectionStroke = 2;
    private const double ChipGap = 6;
    private const double HintMargin = 16;

    private readonly int _maxImageDimension;
    private readonly NativeMethods.RECT _monitor;
    private readonly List<Rectangle> _handles = [];

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
        CreateHandles();

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
        Keyboard.Focus(this);
        Log.Debug("遮罩窗口就绪：ActualSize={Width}x{Height}，Scale=({ScaleX},{ScaleY})",
            ActualWidth, ActualHeight, ScaleX, ScaleY);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScaleFromActualSize();
        UpdateScrimGeometry();
        UpdateHintChip();
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

    private void CreateHandles()
    {
        for (var i = 0; i < 8; i++)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = (Brush)FindResource("Brush.Capture.Selection"),
                Visibility = Visibility.Collapsed,
            };
            _handles.Add(handle);
            HandleLayer.Children.Add(handle);
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
        SelectionBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SizeChip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible)
        {
            foreach (var handle in _handles)
            {
                handle.Visibility = Visibility.Collapsed;
            }

            UpdateScrimGeometry();
            return;
        }

        // 描边向内收 1 DIP，避免 2 DIP 描边被窗口边缘裁掉一半
        SelectionBorder.Width = Math.Max(0, _selection.Width - SelectionStroke);
        SelectionBorder.Height = Math.Max(0, _selection.Height - SelectionStroke);
        Canvas.SetLeft(SelectionBorder, _selection.X + SelectionStroke / 2);
        Canvas.SetTop(SelectionBorder, _selection.Y + SelectionStroke / 2);

        var points = HandlePoints(_selection);
        for (var i = 0; i < _handles.Count; i++)
        {
            _handles[i].Visibility = Visibility.Visible;
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }

        UpdateSizeChip();
        UpdateScrimGeometry();
    }

    /// <summary>4 角 + 4 边中点，居中对齐在描边上（13.2.4）。</summary>
    private static Point[] HandlePoints(Rect rect)
    {
        var centerX = rect.X + rect.Width / 2;
        var centerY = rect.Y + rect.Height / 2;
        return
        [
            new Point(rect.Left, rect.Top),
            new Point(centerX, rect.Top),
            new Point(rect.Right, rect.Top),
            new Point(rect.Right, centerY),
            new Point(rect.Right, rect.Bottom),
            new Point(centerX, rect.Bottom),
            new Point(rect.Left, rect.Bottom),
            new Point(rect.Left, centerY),
        ];
    }

    /// <summary>尺寸提示条：物理像素（用户关心截图分辨率）+ 超限缩放说明（13.2.4 / AC 7）。</summary>
    private void UpdateSizeChip()
    {
        var physicalWidth = (int)Math.Round(_selection.Width * ScaleX);
        var physicalHeight = (int)Math.Round(_selection.Height * ScaleY);
        var text = $"{physicalWidth} × {physicalHeight} px";

        var fit = CaptureGeometry.FitToMaxDimension(physicalWidth, physicalHeight, _maxImageDimension);
        if (fit.Downscaled)
        {
            text += $"（已按 {fit.Ratio:0.##} 倍识别）";
        }

        SizeText.Text = text;
        SizeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chipWidth = SizeText.DesiredSize.Width + 14;
        var chipHeight = SizeText.DesiredSize.Height + 6;

        // 默认放在选区右上；越界时改到右下（13.2.4）
        var left = Math.Clamp(_selection.Right - chipWidth, 0, Math.Max(0, ActualWidth - chipWidth));
        var top = _selection.Top - chipHeight - ChipGap;
        if (top < 0)
        {
            top = Math.Min(_selection.Bottom + ChipGap, Math.Max(0, ActualHeight - chipHeight));
        }

        Canvas.SetLeft(SizeChip, left);
        Canvas.SetTop(SizeChip, top);
    }

    private void UpdateHintChip()
    {
        HintChip.Visibility = Visibility.Visible;
        HintText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chipWidth = HintText.DesiredSize.Width + 14;
        Canvas.SetLeft(HintChip, Math.Max(0, (ActualWidth - chipWidth) / 2));
        Canvas.SetTop(HintChip, HintMargin);
    }
}
