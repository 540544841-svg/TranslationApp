using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>印讯的轻重：决定右上角那枚状态胶囊与停留时长。</summary>
public enum SealToastLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 右下角「印讯」——取代系统气泡。
/// 与 <see cref="ReplacementToastWindow"/> 同一套无打扰做法：WS_EX_NOACTIVATE 不抢焦点，
/// WS_EX_TRANSPARENT 不吃鼠标，逐枚从右下角往上码放，到时自己淡出。
/// </summary>
public partial class SealToastWindow : Window
{
    private const int EdgeMarginDip = 16;
    private const int BottomMarginDip = 8;
    private const int GapDip = 10;
    /// <summary>
    /// 窗口内容四周留的透明边（印面卡在 XAML 里 Margin="18"，那圈是给投影用的）。
    /// 窗口边缘比看得见的卡片多出这么多，算落点时必须补回来，否则卡片会离屏幕边缘远出一圈。
    /// </summary>
    private const int ShadowMarginDip = 18;
    private const int MaxVisible = 3;

    private static readonly List<SealToastWindow> Open = [];

    private readonly DispatcherTimer _hideTimer = new();

    private IntPtr _hwnd;
    private bool _closing;

    public SealToastWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => FadeOut();
    }

    /// <summary>弹一枚印讯（必须从 UI 线程调用；别的线程请用 <see cref="Post"/>）。</summary>
    public static void ShowToast(string title, string message, SealToastLevel level = SealToastLevel.Info)
    {
        // 版面上限：先关最早那枚。FadeOut 会立刻置 _closing，所以这一轮一定会收敛。
        while (Open.Count(item => !item._closing) >= MaxVisible)
        {
            Open.First(item => !item._closing).FadeOut();
        }

        var toast = new SealToastWindow();
        toast.Apply(title, message, level);
        Open.Add(toast);

        toast.Show();
        toast.UpdateLayout(); // 高度是 SizeToContent 算出来的，排版跑完才知道实际尺寸
        RelayoutAll();

        toast.BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation)
        {
            toast.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            toast.Opacity = 1;
        }

        toast._hideTimer.Interval = TimeSpan.FromMilliseconds(level switch
        {
            SealToastLevel.Error => 10_000,
            SealToastLevel.Warning => 8_000,
            _ => 6_000,
        });
        toast._hideTimer.Start();
    }

    /// <summary>切到 UI 线程再弹（托盘回调可能在别的线程上）。</summary>
    public static void Post(string title, string message, SealToastLevel level = SealToastLevel.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ShowToast(title, message, level));
    }

    private void Apply(string title, string message, SealToastLevel level)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        if (level == SealToastLevel.Info)
        {
            SeverityTag.Visibility = Visibility.Collapsed;
            return;
        }

        SeverityTag.Style = (Style)FindResource(level == SealToastLevel.Error ? "Tag.Bad" : "Tag.Warn");
        SeverityText.Text = level == SealToastLevel.Error ? "失败" : "提示";
        SeverityTag.Visibility = Visibility.Visible;
    }

    /// <summary>从右下角往上码放：后弹的在最下，先弹的往上顶。</summary>
    private static void RelayoutAll()
    {
        var area = SystemParameters.WorkArea;
        double offset = 0;
        foreach (var toast in Open.Where(item => !item._closing).Reverse())
        {
            var width = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
            toast.Left = area.Right - width - EdgeMarginDip + ShadowMarginDip;
            toast.Top = BottomLimit(toast) - BottomMarginDip + ShadowMarginDip - offset - toast.ActualHeight;
            // 两枚印讯之间只想留 GapDip，但相邻两圈透明边会叠出 2×ShadowMargin，得减掉
            offset += toast.ActualHeight - (2 * ShadowMarginDip) + GapDip;
        }
    }

    /// <summary>
    /// 印讯落点的下边界（DIP）。任务栏自动隐藏时 SystemParameters.WorkArea 可能仍是整屏高，
    /// 照它摆会把印讯塞到任务栏底下；所以直接取任务栏窗口的顶边，任务栏收起或弹出，印讯都停在它上方。
    /// </summary>
    private static double BottomLimit(SealToastWindow toast)
    {
        var work = SystemParameters.WorkArea.Bottom;
        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !NativeMethods.GetWindowRect(tray, out var rect) || rect.Top <= 0)
        {
            return work; // 没有任务栏，或它挪到了屏幕顶部：工作区就是下边界
        }

        var dpi = NativeMethods.GetDpiForWindow(toast._hwnd);
        var pixelsPerDip = dpi > 0 ? dpi / 96.0 : 1.0;
        // 任务栏 rect 是物理像素，WorkArea 是 DIP：必须用 WPF 自己的设备变换换算，两者才在同一坐标系里。
        // GetDpiForWindow 报的是监视器真实 DPI，与 SystemParameters 的 DIP 空间未必一致（1.5× 屏上
        // 会把印讯顶到屏幕中间去），所以这里改取 CompositionTarget 的缩放。
        var scale = PresentationSource.FromVisual(toast)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var taskbarTop = rect.Top / scale;
        return Math.Min(work, taskbarTop);
    }

    private void FadeOut()
    {
        if (_closing)
        {
            return;
        }

        _closing = true; // 先置位：RelayoutAll 与「版面上限」都以它为准
        _hideTimer.Stop();
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
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

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer.Stop();
        Open.Remove(this);
        _closing = true;
        RelayoutAll(); // 补上被抽走的高度，下面几枚跟着落下来
        base.OnClosed(e);
    }
}
