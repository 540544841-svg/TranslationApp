using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>
/// 悬停取词浮标窗（FR-036 / spec §2.2）：拖拽选择结束后跟随光标出现的小圆标，
/// 点击才触发与 Alt+S 同一条取词翻译链路。单例复用同一窗口实例，不叠窗。
/// 定位走 SetWindowPos 物理像素（FR-025 同款：WPF Left/Top 是 DIP，跨屏缩放会偏）。
/// </summary>
public partial class HoverBadgeWindow : Window
{
    /// <summary>浮标物理像素边长（固定像素不随 DPI 放大，避免高屏过大）。</summary>
    private const int BadgeSizePx = 32;

    /// <summary>相对光标的右下偏移。</summary>
    private const int OffsetPx = 14;

    private const int AutoHideMs = 5_000;

    private readonly DispatcherTimer _autoHideTimer;
    private IntPtr _hwnd;
    private (int X, int Y) _pendingPosition;
    private (int X, int Y) _position;

    /// <summary>浮标被点击（App 接线到划词链路）。</summary>
    public event Action? BadgeClicked;

    public HoverBadgeWindow()
    {
        InitializeComponent();
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _autoHideTimer.Tick += (_, _) => HideBadge();
    }

    /// <summary>在光标物理坐标处显示；已显示则仅移动位置并重排 5s 自动隐藏。</summary>
    public void ShowAt(int physicalX, int physicalY)
    {
        var (x, y) = ClampToWorkArea(physicalX + OffsetPx, physicalY + OffsetPx);
        if (!IsVisible)
        {
            Show();
        }

        if (_hwnd == IntPtr.Zero)
        {
            _pendingPosition = (x, y); // 首次 Show 后句柄尚未初始化：OnSourceInitialized 里补摆
            return;
        }

        ApplyPosition(x, y);
    }

    /// <summary>隐藏浮标并停表（被点击 / 下一次按下 / 超时都走这里）。</summary>
    public void HideBadge()
    {
        _autoHideTimer.Stop();
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyPosition(_pendingPosition.X, _pendingPosition.Y);
    }

    private void ApplyPosition(int x, int y)
    {
        _position = (x, y);
        ScreenInterop.SetWindowPos(
            _hwnd, ScreenInterop.HWND_TOPMOST, x, y, BadgeSizePx, BadgeSizePx,
            ScreenInterop.SWP_NOACTIVATE | ScreenInterop.SWP_SHOWWINDOW);
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    /// <summary>
    /// 物理点是否落在浮标上。钩子按下事件**先于** WPF 点击到达：
    /// App 的「按下即隐藏」必须跳过点向浮标的这一下，否则点击永远打不中。
    /// </summary>
    public bool HitTestBadge(int physicalX, int physicalY) =>
        IsVisible
        && physicalX >= _position.X && physicalX < _position.X + BadgeSizePx
        && physicalY >= _position.Y && physicalY < _position.Y + BadgeSizePx;

    /// <summary>夹到坐标所在显示器的工作区内（多屏取所在屏；取不到信息就原样放，绝不挡取词）。</summary>
    private static (int X, int Y) ClampToWorkArea(int x, int y)
    {
        if (!ScreenInterop.TryGetMonitorAt(x, y, out var info, out _))
        {
            return (x, y);
        }

        var left = Math.Clamp(x, info.RcWork.Left, Math.Max(info.RcWork.Left, info.RcWork.Right - BadgeSizePx));
        var top = Math.Clamp(y, info.RcWork.Top, Math.Max(info.RcWork.Top, info.RcWork.Bottom - BadgeSizePx));
        return (left, top);
    }

    private void OnBadgeClick(object sender, MouseButtonEventArgs e)
    {
        HideBadge();
        BadgeClicked?.Invoke();
    }
}
