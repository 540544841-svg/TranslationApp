using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>
/// 悬停取词浮标窗（FR-036 / spec §2.2）：确认「文字真的被选中」之后，跟着光标浮出的那一枚印。
/// 点击才触发与 Alt+S 同一条取词链路。单例复用同一窗口实例，不叠窗。
/// 定位走 SetWindowPos 物理像素（FR-025 同款：WPF Left/Top 是 DIP，跨屏缩放会偏），
/// 尺寸固定物理像素、内容用 Viewbox 缩放，所以高 DPI 屏上印面不会被裁掉。
/// </summary>
public partial class HoverBadgeWindow : Window
{
    /// <summary>浮标物理像素边长（固定像素不随 DPI 放大，避免高屏过大）；整窗都是点击区。</summary>
    private const int BadgeSizePx = 40;

    /// <summary>光标到印面左上角的右下偏移（物理像素）。</summary>
    private const int OffsetPx = 14;

    /// <summary>印面在 40 单位设计空间里四周缩进的量（40 − 32 再对半）。</summary>
    private const int SealInsetPx = 4;

    private const int AutoHideMs = 5_000;

    /// <summary>入场（落印）：下沉过冲后回弹，与工作台译文落定同一个曲线。</summary>
    private static readonly TimeSpan StampDuration = TimeSpan.FromMilliseconds(240);

    /// <summary>墨晕：比印面活得久一点，让人看见它散开。</summary>
    private static readonly TimeSpan MistDuration = TimeSpan.FromMilliseconds(420);

    /// <summary>退场淡出：比入场短，免得收得拖泥带水。</summary>
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>指针反馈的位移量。</summary>
    private const double PointerHoverScale = 1.05;
    private const double PointerPressedScale = 0.94;

    private readonly DispatcherTimer _autoHideTimer;
    private IntPtr _hwnd;
    private (int X, int Y) _position;
    private bool _fading;
    private bool _pointerOver;
    private int _showGeneration;

    /// <summary>浮标被点击（App 接线到划词链路）。</summary>
    public event Action? BadgeClicked;

    public HoverBadgeWindow()
    {
        InitializeComponent();
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _autoHideTimer.Tick += (_, _) => HideBadge();

        // 句柄先建好：ShowAt 从此只有「Show 之后立刻 SetWindowPos」一条路。原来首次 Show 靠
        // OnSourceInitialized 补摆、后续 Show 什么都不做，于是第二枚以后的印都停在上一次的
        // 坐标上——看起来就是「浮标不贴着选区」。
        _hwnd = new WindowInteropHelper(this).EnsureHandle();

        // WS_EX_NOACTIVATE（同 SealToastWindow）：点浮标不能把前台抢过来。抢过来之后模拟的
        // Ctrl+C 会发给我们自己，取词必然落空，小窗只能退化成手动输入的空壳。
        var extendedStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(extendedStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
    }

    /// <summary>在光标物理坐标处显示；已显示则仅移动位置、重放落印并重排自动隐藏。</summary>
    public void ShowAt(int physicalX, int physicalY)
    {
        var (x, y) = ClampToWorkArea(physicalX + OffsetPx - SealInsetPx, physicalY + OffsetPx - SealInsetPx);
        _position = (x, y);
        _showGeneration++;

        if (!IsVisible)
        {
            Show();
        }

        CancelFade();
        ApplyPosition(x, y);
        PlayStamp();
    }

    /// <summary>隐藏浮标（被点击 / 下一次按下 / 超时都走这里）；开启动画时先淡出再收。</summary>
    public void HideBadge()
    {
        _autoHideTimer.Stop();
        _pointerOver = false;
        if (!IsVisible || _fading)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            ResetVisuals();
            Hide();
            return;
        }

        _fading = true;
        var generation = _showGeneration;
        var fade = new DoubleAnimation(Opacity, 0, FadeOutDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (generation != _showGeneration)
            {
                return; // 淡出途中又落了一印：这一枚已经不作数了
            }

            _fading = false;
            BeginAnimation(OpacityProperty, null);
            ResetVisuals();
            Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 物理点是否落在浮标上。钩子按下事件**先于** WPF 点击到达：
    /// App 的「按下即隐藏」必须跳过点向浮标的这一下，否则点击永远打不中。
    /// 淡出中的浮标一律算「不在」——它已经准备走了，别再拦下一次按下。
    /// </summary>
    public bool HitTestBadge(int physicalX, int physicalY) =>
        IsVisible
        && !_fading
        && physicalX >= _position.X && physicalX < _position.X + BadgeSizePx
        && physicalY >= _position.Y && physicalY < _position.Y + BadgeSizePx;

    private void ApplyPosition(int x, int y)
    {
        _position = (x, y);
        ScreenInterop.SetWindowPos(
            _hwnd, ScreenInterop.HWND_TOPMOST, x, y, BadgeSizePx, BadgeSizePx,
            ScreenInterop.SWP_NOACTIVATE | ScreenInterop.SWP_SHOWWINDOW);
        _autoHideTimer.Stop();
        if (!_pointerOver)
        {
            _autoHideTimer.Start();
        }
    }

    /// <summary>落印：小一号压下来 → 过冲 → 归位。位移换成缩放，免得被 40px 的窗口裁掉。</summary>
    private void PlayStamp()
    {
        BeginAnimation(OpacityProperty, null);
        SealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        SealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        Halo.BeginAnimation(OpacityProperty, null);

        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        var overshoot = new KeySpline(0.34, 1.45, 0.5, 1);
        var land = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120));
        var bounce = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(186));
        var end = KeyTime.FromTimeSpan(StampDuration);

        var scale = new DoubleAnimationUsingKeyFrames
        {
            Duration = StampDuration,
            FillBehavior = FillBehavior.Stop,
        };
        scale.KeyFrames.Add(new SplineDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.Zero), overshoot));
        scale.KeyFrames.Add(new SplineDoubleKeyFrame(1.06, land, overshoot));
        scale.KeyFrames.Add(new SplineDoubleKeyFrame(0.98, bounce, overshoot));
        scale.KeyFrames.Add(new SplineDoubleKeyFrame(1, end, overshoot));
        SealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        SealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);

        var appear = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        BeginAnimation(OpacityProperty, appear);

        var mistScale = new DoubleAnimation(0.5, 2, MistDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, mistScale);
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, mistScale);
        Halo.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.55, 0, MistDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void CancelFade()
    {
        _fading = false;
        BeginAnimation(OpacityProperty, null);
    }

    private void ResetVisuals()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        SealPointerScale.ScaleX = 1;
        SealPointerScale.ScaleY = 1;
    }

    private void OnBadgePointerEnter(object sender, MouseEventArgs e)
    {
        _pointerOver = true;
        _autoHideTimer.Stop(); // 指针停在印上就先别收
        AnimatePointerScale(PointerHoverScale);
    }

    private void OnBadgePointerLeave(object sender, MouseEventArgs e)
    {
        _pointerOver = false;
        AnimatePointerScale(1);
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void OnBadgePointerDown(object sender, MouseButtonEventArgs e) => AnimatePointerScale(PointerPressedScale);

    private void AnimatePointerScale(double target)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SealPointerScale.ScaleX = target;
            SealPointerScale.ScaleY = target;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(150);
        SealPointerScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        SealPointerScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

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
