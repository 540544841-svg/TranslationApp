using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>
/// Non-activating replacement confirmation. It never steals focus or mouse input
/// from the target application, so Ctrl+Z remains available immediately.
/// </summary>
public partial class ReplacementToastWindow : Window
{
    private const int CursorOffsetPx = 16;
    private const int AutoHideMs = 1700;

    private readonly DispatcherTimer _hideTimer;
    private IntPtr _hwnd;

    public ReplacementToastWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _hideTimer.Tick += (_, _) => HideToast();
    }

    public void ShowMessage(string message)
    {
        MessageText.Text = message;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        PositionNearCursor();

        BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        }
        else
        {
            Opacity = 1;
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void HideToast()
    {
        _hideTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(extendedStyle | NativeMethods.WS_EX_NOACTIVATE
                                   | NativeMethods.WS_EX_TOOLWINDOW
                                   | NativeMethods.WS_EX_TRANSPARENT));
    }

    private void PositionNearCursor()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var point = new NativeMethods.POINT();
        if (!NativeMethods.GetPhysicalCursorPos(ref point)
            && !NativeMethods.GetCursorPos(ref point))
        {
            return;
        }

        var scale = ScreenInterop.TryGetMonitorAt(point.X, point.Y, out var info, out var monitorScale)
            ? monitorScale
            : 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        var x = point.X + CursorOffsetPx;
        var y = point.Y + CursorOffsetPx;

        if (info.RcWork.Right > info.RcWork.Left)
        {
            x = Math.Clamp(x, info.RcWork.Left, Math.Max(info.RcWork.Left, info.RcWork.Right - width));
            y = Math.Clamp(y, info.RcWork.Top, Math.Max(info.RcWork.Top, info.RcWork.Bottom - height));
        }

        ScreenInterop.SetWindowPos(
            _hwnd,
            ScreenInterop.HWND_TOPMOST,
            x,
            y,
            width,
            height,
            ScreenInterop.SWP_NOACTIVATE | ScreenInterop.SWP_SHOWWINDOW);
    }
}
