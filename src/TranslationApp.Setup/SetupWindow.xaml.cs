using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

// System.Windows.Shapes 里也有一个 Path（图形），和 System.IO.Path 撞名；显式起个别名。
using IOPath = System.IO.Path;

namespace TranslationApp.Setup;

/// <summary>
/// 立契窗口。设计稿第 04 节的 1:1 落地：四枚方菱形组成的进度脊、一屏一个决定、
/// 一枚印面在「大印槽 → 进度脊 → 落款处」之间飞，最后砸在契书上。
/// </summary>
public partial class SetupWindow : Window
{
    /// <summary>最后一步（契成）。</summary>
    public const int LastStep = 4;

    /// <summary>收印是第 5 屏，不占进度脊的四格，单独给一个编号。</summary>
    public const int UnsealStep = 99;

    private static readonly string[] StepNames = ["启封", "落址", "接引擎", "盖印"];

    private static readonly string[] Hints =
    [
        "全程本机完成 · 不联网也能装",
        "便携与常规安装随时可切",
        "密钥不出本机 · 不经中转",
        "开印后无需重启",
        "契已成立 · 印已落",
    ];

    private static readonly IEasingFunction Decelerate = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction Accelerate = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction StampEase = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };

    private readonly SetupSession _session;
    private readonly InstallService _service;
    private readonly List<WorkRow> _workRows = [];

    private int _step;
    private bool _busy;

    /// <summary>出图模式：不播动画，所有状态直接落定到终态。</summary>
    private bool _render;

    /// <summary>收印屏是个岔路：进去之后上一步/继续都该回到进来前那一屏，而不是接着往下走。</summary>
    private bool _unsealPane;
    private int _returnStep;

    internal SetupWindow(SetupSession session)
    {
        _session = session;
        _service = new InstallService(session.Payload);

        InitializeComponent();
        BuildStaticCopy();
        RefreshDecisions();
        EngineList.ItemsSource = EngineOption.All;
        EngineList.SelectedIndex = 0;
        EngineList.SelectionChanged += (_, _) => RefreshDecisions();

        AutoStartToggle.Checked += (_, _) => RefreshDecisions();
        AutoStartToggle.Unchecked += (_, _) => RefreshDecisions();
        DesktopToggle.Checked += (_, _) => RefreshDecisions();
        DesktopToggle.Unchecked += (_, _) => RefreshDecisions();

        Loaded += async (_, _) => await GoToAsync(0, animate: true);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>出图用的稳定文件名片段。</summary>
    public static string StepKey(int step) => step switch
    {
        0 => "welcome",
        1 => "place",
        2 => "engine",
        3 => "seal",
        4 => "contract",
        _ => "unseal",
    };

    // ============================================================
    // 文案与决定
    // ============================================================
    private void BuildStaticCopy()
    {
        WelcomeBody.Text = "划词、截图、整段，译完才落印。\n契成之后，一切都还能在设置里改。";
        WelcomeFacts.Children.Add(Fact("单文件", "无需运行库，卸载不留残"));
        WelcomeFacts.Children.Add(Fact("DPAPI", "密钥只在本机加密落盘"));
        WelcomeFacts.Children.Add(Fact("Alt + D", "默认呼出热键，随时可改"));

        WhyText.Text =
            "开机后只驻托盘、不占窗口，Alt+D 随时呼出；不需要可随时关掉。" +
            "印谱落址自动判断：装在固定盘写用户目录，装进 U 盘就跟着程序走。";
        EngineHint.Text = "密钥留到「设置 → 引擎」里再填也行；这里只决定先用哪一方。";
    }

    private static UIElement Fact(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = new TextBlock
        {
            Text = key,
            FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Brush.TextPrimary"],
            Margin = new Thickness(0, 6, 0, 6),
        };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["Brush.TextTertiary"],
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 6),
        };
        Grid.SetColumn(v, 1);
        row.Children.Add(k);
        row.Children.Add(v);

        var host = new StackPanel();
        host.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["Brush.Border"],
        });
        host.Children.Add(row);
        return host;
    }

    private InstallOptions CurrentOptions() => new(
        _session.InstallDirectory,
        AutoStartToggle.IsChecked == true,
        DesktopToggle.IsChecked == true,
        StartMenuShortcut: true,
        _session.Engine);

    private void RefreshDecisions()
    {
        _session.Engine = EngineList.SelectedItem as EngineOption ?? EngineOption.Bing;

        PathText.Text = _session.InstallDirectory;
        SizeText.Text = InstallService.FormatSize(_service.PayloadBytes) + (PayloadStore.Current.Present ? "" : "（包里没有主程序）");
        PortableText.Text = IsRemovable(_session.InstallDirectory) ? "随盘走 · <安装目录>\\data" : "自动 · 用户目录";

        EngineNote.Text = _session.Engine.Note;
        EngineRows.Children.Clear();
        EngineRows.Children.Add(SideRow("调用方式", _session.Engine.NeedsKey ? "HTTPS · 需密钥" : "HTTPS · 免登录"));
        EngineRows.Children.Add(SideRow("密钥存储", _session.Engine.NeedsKey ? "本机 DPAPI 加密" : "不使用密钥"));

        RevPath.Text = _session.InstallDirectory;
        RevEngine.Text = _session.Engine.ShortName;
        RevHotkey.Text = "Alt + D";
        RevResident.Text = AutoStartToggle.IsChecked == true ? "仅托盘" : "不常驻";

        RefreshUnsealPane();
    }

    private static UIElement SideRow(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = key,
            FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["Brush.TextTertiary"],
            Margin = new Thickness(0, 8, 0, 8),
        });
        var v = new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"],
            Margin = new Thickness(0, 8, 0, 8),
        };
        Grid.SetColumn(v, 1);
        row.Children.Add(v);

        var host = new StackPanel();
        host.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["Brush.Border"],
        });
        host.Children.Add(row);
        return host;
    }

    private static bool IsRemovable(string directory)
    {
        try
        {
            var root = IOPath.GetPathRoot(IOPath.GetFullPath(directory));
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    // 导航
    // ============================================================
    private async Task GoToAsync(int step, bool animate)
    {
        step = Math.Clamp(step, 0, LastStep);
        _step = step;

        ShowPane(step);
        UpdateRail(step);

        StepText.Text = step < 4 ? $"第 {step + 1} / 4 步 · {StepNames[step]}" : "契已成立";
        HintText.Text = Hints[step];
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step switch { 3 => "落印", 4 => "启印", _ => "继续" };
        UnsealButton.IsEnabled = true;

        Fly.Opacity = 1;
        FlyRotate.Angle = 0;

        if (step == 4)
        {
            MoveFly(3, animate ? 320 : 0);
            if (animate)
            {
                await DelayAsync(350);
            }

            MoveFly(4, animate ? 400 : 0);
            if (animate)
            {
                await DelayAsync(430);
                await StampAsync();
            }
            else
            {
                ShowStampedState();
            }
        }
        else
        {
            MoveFly(step, animate ? (step == 0 ? 400 : 340) : 0);
        }
    }

    private UIElement[] Panes => [Pane0, Pane1, Pane2, Pane3, Pane4, Pane5];

    private void ShowPane(int index)
    {
        var panes = Panes;
        for (var i = 0; i < panes.Length; i++)
        {
            // 上一轮可能还留着一条 HoldEnd 的淡入动画，动画优先级高于本地值——先清掉再改。
            panes[i].BeginAnimation(UIElement.OpacityProperty, null);
            panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            panes[i].Opacity = i == index ? 0 : 0;
        }

        var pane = panes[index];
        if (pane.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            pane.RenderTransform = translate;
        }

        // 出图时没有动画时钟推进，动画会停在起始值（整屏空白）——直接落定到终态。
        if (_render)
        {
            Snap(translate, TranslateTransform.YProperty, 0);
            pane.Opacity = 1;
            return;
        }

        Animate(pane, UIElement.OpacityProperty, 0, 1, 220, Decelerate);
        Animate(translate, TranslateTransform.YProperty, -6, 0, 220, Decelerate);
    }

    private void UpdateRail(int step)
    {
        var dots = new[] { SlotDot0, SlotDot1, SlotDot2, SlotDot3 };
        var halos = new[] { SlotHalo0, SlotHalo1, SlotHalo2, SlotHalo3 };
        var labels = new[] { SlotText0, SlotText1, SlotText2, SlotText3 };
        var segments = new[] { Seg0, Seg1, Seg2, Seg3 };

        var border = (Brush)Application.Current.Resources["Brush.BorderStrong"];
        var primary = (Brush)Application.Current.Resources["Brush.Primary"];

        for (var i = 0; i < dots.Length; i++)
        {
            var done = i < step;
            var on = i == step;
            dots[i].Fill = done ? primary : Brushes.Transparent;
            dots[i].Stroke = done || on ? primary : border;
            dots[i].StrokeDashArray = done || on ? null : new DoubleCollection([1.6, 1.2]);
            halos[i].Opacity = on ? 1 : 0;
            labels[i].Foreground = on
                ? (Brush)Application.Current.Resources["Brush.TextPrimary"]
                : (Brush)Application.Current.Resources["Brush.TextTertiary"];
            labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            segments[i].Background = done ? primary : border;
            segments[i].Opacity = done ? 1 : 0.42;
        }
    }

    // ============================================================
    // 飞印与落印
    // ============================================================
    private (Point Center, double Size) AnchorFor(int step)
    {
        Root.UpdateLayout();
        return step switch
        {
            0 => (CenterOf(AnchorWelcome), 64),
            <= 3 => (CenterOf(new[] { SlotDot0, SlotDot1, SlotDot2 }[step - 1]), 15),
            UnsealStep => (CenterOf(UnsealSealSlot), 66),
            _ => (CenterOf(SignSpot), 66),
        };
    }

    private Point CenterOf(FrameworkElement element)
    {
        var point = element.TransformToAncestor(Root)
            .Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return point;
    }

    private void MoveFly(int step, double durationMs)
    {
        var (center, size) = AnchorFor(step);
        var scale = size / 64.0;
        var x = center.X - 32;
        var y = center.Y - 32;

        if (durationMs <= 0)
        {
            Snap(FlyMove, TranslateTransform.XProperty, x);
            Snap(FlyMove, TranslateTransform.YProperty, y);
            Snap(FlyScale, ScaleTransform.ScaleXProperty, scale);
            Snap(FlyScale, ScaleTransform.ScaleYProperty, scale);
            return;
        }

        Animate(FlyMove, TranslateTransform.XProperty, FlyMove.X, x, durationMs, Decelerate);
        Animate(FlyMove, TranslateTransform.YProperty, FlyMove.Y, y, durationMs, Decelerate);
        Animate(FlyScale, ScaleTransform.ScaleXProperty, FlyScale.ScaleX, scale, durationMs, Decelerate);
        Animate(FlyScale, ScaleTransform.ScaleYProperty, FlyScale.ScaleY, scale, durationMs, Decelerate);
    }

    private async Task StampAsync()
    {
        var (center, size) = AnchorFor(4);
        var scale = size / 64.0;
        var x = center.X - 32;
        var y = center.Y - 32;

        // 抬手：印面微微抬起、偏一点角度——不抬手就没有「砸」的势能
        Animate(FlyMove, TranslateTransform.XProperty, x, x, 120, null);
        Animate(FlyMove, TranslateTransform.YProperty, y, y - 26, 120, null);
        Animate(FlyRotate, RotateTransform.AngleProperty, 0, -8, 120, null);
        await DelayAsync(130);

        // 砸落
        Animate(FlyMove, TranslateTransform.YProperty, y - 26, y, 110, Accelerate);
        await DelayAsync(120);

        // 触纸瞬间：压扁 + 三圈墨晕 + 落款处的虚线框换成真印
        Snap(FlyScale, ScaleTransform.ScaleXProperty, scale * 1.28);
        Snap(FlyScale, ScaleTransform.ScaleYProperty, scale * 0.7);
        ExpandRings();
        SignSpot.Opacity = 0;
        SignSpotText.Opacity = 0;
        GhostSeal.Opacity = 1;
        HintText.Text = "契已成立 · 印已落";

        // 回弹
        Animate(FlyScale, ScaleTransform.ScaleXProperty, scale * 1.28, scale, 210, StampEase);
        Animate(FlyScale, ScaleTransform.ScaleYProperty, scale * 0.7, scale, 210, StampEase);
        Animate(FlyRotate, RotateTransform.AngleProperty, -8, 0, 210, StampEase);
        await DelayAsync(240);

        // 真印留在契书上，飞印收场
        Animate(Fly, UIElement.OpacityProperty, 1, 0, 260, null);
    }

    private void ShowStampedState()
    {
        var (center, size) = AnchorFor(4);
        var scale = size / 64.0;
        Snap(FlyMove, TranslateTransform.XProperty, center.X - 32);
        Snap(FlyMove, TranslateTransform.YProperty, center.Y - 32);
        Snap(FlyScale, ScaleTransform.ScaleXProperty, scale);
        Snap(FlyScale, ScaleTransform.ScaleYProperty, scale);
        Fly.Opacity = 0;
        SignSpot.Opacity = 0;
        SignSpotText.Opacity = 0;
        GhostSeal.Opacity = 1;
    }

    private void ExpandRings()
    {
        GhostRings.Children.Clear();
        for (var i = 0; i < 3; i++)
        {
            var ring = new Ellipse
            {
                Width = 66,
                Height = 66,
                Stroke = (Brush)Application.Current.Resources["Brush.Primary"],
                StrokeThickness = 1.2,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.5, 0.5),
                Opacity = 0.8,
            };
            Canvas.SetLeft(ring, 5);
            Canvas.SetTop(ring, 5);
            GhostRings.Children.Add(ring);

            var delay = TimeSpan.FromMilliseconds(i * 46);
            var scale = (ScaleTransform)ring.RenderTransform;
            Animate(scale, ScaleTransform.ScaleXProperty, 0.5, 1.9, 420, Decelerate, delay);
            Animate(scale, ScaleTransform.ScaleYProperty, 0.5, 1.9, 420, Decelerate, delay);
            Animate(ring, UIElement.OpacityProperty, 0.8, 0, 400, null, delay);
        }
    }

    // ============================================================
    // 盖印：真正干活
    // ============================================================
    private async Task RunInstallAsync()
    {
        _busy = true;
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        UnsealButton.IsEnabled = false;
        ErrorBanner.Visibility = Visibility.Collapsed;

        var options = CurrentOptions();
        var plan = _service.BuildPlan(options);
        BuildWorkList(plan);
        SealMessage.Text = "正在开印…";
        SetProgress(0);

        var started = DateTime.UtcNow;
        for (var i = 0; i < plan.Count; i++)
        {
            await DelayAsync(110);
            try
            {
                await plan[i].Execute(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _workRows[i].Fail();
                ErrorText.Text = ex.Message;
                ErrorBanner.Visibility = Visibility.Visible;
                SealMessage.Text = "没盖上 · 印还在手上";
                _busy = false;
                NextButton.IsEnabled = true;
                NextButton.Content = "再试一次";
                UnsealButton.IsEnabled = true;
                return;
            }

            _workRows[i].Done();
            SetProgress((i + 1) / (double)plan.Count);
            await DelayAsync(190);
        }

        SealMessage.Text = "印已落 · 契成";
        _session.Report = new InstallReport(
            options.InstallDirectory,
            InstallLayout.DataDirectory(options.InstallDirectory),
            options.Engine,
            options.AutoStart,
            options.DesktopShortcut,
            options.StartMenuShortcut,
            _service.PayloadBytes,
            _service.Version,
            DateTime.UtcNow - started);

        BuildContract(_session.Report);
        _busy = false;
        await GoToAsync(4, animate: true);
    }

    private void SetProgress(double fraction)
    {
        if (ProgressFill.Parent is not Border track)
        {
            return;
        }

        ProgressFill.Width = Math.Max(0, track.ActualWidth * Math.Clamp(fraction, 0, 1));
    }

    private void BuildWorkList(IReadOnlyList<InstallStep> plan)
    {
        WorkList.Children.Clear();
        _workRows.Clear();

        foreach (var step in plan)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tick = new Rectangle
            {
                Width = 12,
                Height = 12,
                RadiusX = 3,
                RadiusY = 3,
                StrokeThickness = 1,
                Stroke = (Brush)Application.Current.Resources["Brush.BorderStrong"],
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(45),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 10, 0),
            };
            var title = new TextBlock
            {
                Text = step.Title,
                FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["Brush.TextTertiary"],
                Margin = new Thickness(0, 3, 0, 3),
            };
            var note = new TextBlock
            {
                Text = step.Note,
                FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
                FontSize = 10,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["Brush.TextTertiary"],
            };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(note, 2);
            grid.Children.Add(tick);
            grid.Children.Add(title);
            grid.Children.Add(note);

            // 设计稿：工作项之间是一条虚线——读起来像一张清单，而不是一坨文字。
            var seam = new System.Windows.Shapes.Path
            {
                Data = new LineGeometry(new Point(0, 0), new Point(100, 0)),
                Stretch = Stretch.Fill,
                Height = 1,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection([1.6, 1.6]),
                Stroke = (Brush)Application.Current.Resources["Brush.Border"],
            };
            Grid.SetRow(seam, 1);
            Grid.SetColumnSpan(seam, 3);
            grid.Children.Add(seam);

            var host = new StackPanel { Opacity = 0.5 };
            host.Children.Add(grid);
            WorkList.Children.Add(host);
            _workRows.Add(new WorkRow(host, tick, title));
        }
    }

    private void BuildContract(InstallReport report)
    {
        ContractRows.Children.Clear();
        ContractRows.Children.Add(ContractRow("版 本", $"{report.Version} · 单文件"));
        ContractRows.Children.Add(ContractRow("落 址", report.InstallDirectory));
        ContractRows.Children.Add(ContractRow("印 谱", report.DataDirectory));
        ContractRows.Children.Add(ContractRow("引 擎", report.Engine.ShortName));
        ContractRows.Children.Add(ContractRow("热 键", "Alt + D"));
        ContractRows.Children.Add(ContractRow("常 驻", report.AutoStart ? "仅托盘 · 开机自启" : "不常驻"));
        ContractRows.Children.Add(ContractRow("立 契", "本机 · 当前用户"));
        ContractRows.Children.Add(ContractRow("留 痕", "收印时一并抹去"));
    }

    private static UIElement ContractRow(string key, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = key,
            FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
            FontSize = 10.5,
            Foreground = (Brush)Application.Current.Resources["Brush.TextTertiary"],
            Margin = new Thickness(0, 0, 14, 0),
        });

        var v = new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)Application.Current.Resources["Font.Mono"],
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    // ============================================================
    // 收印
    // ============================================================
    private void RefreshUnsealPane()
    {
        if (UnsealRows is null)
        {
            return;
        }

        UnsealRows.Children.Clear();
        if (InstallLayout.TryGetInstalledDirectory(out var installed))
        {
            UnsealRows.Children.Add(ContractRow("落 址", installed));
            UnsealRows.Children.Add(ContractRow("印 谱", InstallLayout.DataDirectory(installed)));
            UnsealRows.Children.Add(ContractRow("会抹掉", "程序 · 快捷方式 · 开机自启 · 应用列表登记"));
            UnsealRows.Children.Add(ContractRow("要选一个", "印谱与藏印留不留"));
            UnsealAllButton.IsEnabled = true;
            UnsealKeepButton.IsEnabled = true;
        }
        else
        {
            UnsealRows.Children.Add(ContractRow("状 态", "本机没有找到译印的收印登记"));
            UnsealRows.Children.Add(ContractRow("说 明", "没装过就不必收，关掉窗口即可"));
            UnsealAllButton.IsEnabled = false;
            UnsealKeepButton.IsEnabled = false;
        }
    }

    // ============================================================
    // 事件
    // ============================================================
    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_unsealPane)
        {
            await LeaveUnsealAsync();
            return;
        }

        if (_step == 3)
        {
            await RunInstallAsync();
            return;
        }

        if (_step == LastStep)
        {
            // 启印：把刚装好的主程序叫起来，安装器功成身退
            LaunchInstalledApp();
            Close();
            return;
        }

        await GoToAsync(_step + 1, animate: true);
    }

    private async void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _step == 0)
        {
            return;
        }

        if (_unsealPane)
        {
            await LeaveUnsealAsync();
            return;
        }

        await GoToAsync(_step - 1, animate: true);
    }

    private void OnUnsealClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        ShowPane(5);
        StepText.Text = "收印";
        _unsealPane = true;
        _returnStep = _step;
        HintText.Text = "收印之后要重装才能回来 · 看清再落章";
        BackButton.IsEnabled = true;
        NextButton.Content = "启印";
        UnsealButton.IsEnabled = false;
        Fly.Opacity = 1;
        MoveFly(UnsealStep, 260);
    }

    /// <summary>从收印屏回上一屏：恢复飞印与按钮，不能把用户困在这一屏。</summary>
    private async Task LeaveUnsealAsync()
    {
        _unsealPane = false;
        UnsealButton.IsEnabled = true;
        await GoToAsync(_returnStep, animate: true);
    }

    private void OnUnsealKeepClick(object sender, RoutedEventArgs e) => RunUnseal(userData: false);

    private void OnUnsealAllClick(object sender, RoutedEventArgs e) => RunUnseal(userData: true);

    private void RunUnseal(bool userData)
    {
        if (!InstallLayout.TryGetInstalledDirectory(out var installed))
        {
            return;
        }

        InstallService.Unseal(installed, userData);
        RefreshUnsealPane();
        HintText.Text = "印已收回 · 本机不再留痕";

        // 印被收走：抬起来、偏一点角度、淡出——槽位重新空出来
        Animate(FlyMove, TranslateTransform.YProperty, FlyMove.Y, FlyMove.Y - 30, 260, Decelerate);
        Animate(FlyRotate, RotateTransform.AngleProperty, 0, -6, 260, Decelerate);
        Animate(Fly, UIElement.OpacityProperty, 1, 0, 420, null);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "印放在哪，数据就放在哪",
            InitialDirectory = Directory.Exists(_session.InstallDirectory)
                ? _session.InstallDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _session.InstallDirectory = IOPath.Combine(dialog.FolderName, "Inkseal");
            RefreshDecisions();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !_busy:
                OnNextClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Escape when !_busy && (_step > 0 || _unsealPane):
                if (_unsealPane)
                {
                    await LeaveUnsealAsync();
                }
                else
                {
                    await GoToAsync(_step - 1, animate: true);
                }

                e.Handled = true;
                break;
        }
    }

    private void LaunchInstalledApp()
    {
        var target = InstallLayout.AppPath(_session.InstallDirectory);
        try
        {
            if (File.Exists(target))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = _session.InstallDirectory,
                });
            }
        }
        catch
        {
            // 起不来不是安装失败：快捷方式与落址都已就位，用户自己双击也能开
        }
    }

    // ============================================================
    // 出图支持：把某一屏摆成静止状态（不播动画），供离屏渲染核对版面
    // ============================================================
    public void PrepareForRender(int step)
    {
        _render = true;
        Root.Measure(new Size(Width, Height));
        Root.Arrange(new Rect(0, 0, Width, Height));
        Root.UpdateLayout();

        if (step == UnsealStep)
        {
            RefreshDecisions();
            ShowPane(5);
            StepText.Text = "收印";
            HintText.Text = "印已收回 · 本机不再留痕";
            BackButton.IsEnabled = true;
            NextButton.Content = "启印";
            Fly.Opacity = 1;
            MoveFly(UnsealStep, 0);
            Root.UpdateLayout();
            return;
        }

        _session.Report ??= new InstallReport(
            _session.InstallDirectory,
            InstallLayout.DataDirectory(_session.InstallDirectory),
            _session.Engine,
            AutoStartToggle.IsChecked == true,
            DesktopToggle.IsChecked == true,
            true,
            _service.PayloadBytes,
            _service.Version,
            TimeSpan.FromSeconds(3.4));

        BuildContract(_session.Report);
        ShowPane(step);
        UpdateRail(step);
        StepText.Text = step < 4 ? $"第 {step + 1} / 4 步 · {StepNames[step]}" : "契已成立";
        HintText.Text = Hints[step];
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step switch { 3 => "落印", 4 => "启印", _ => "继续" };

        if (step == 3)
        {
            var plan = _service.BuildPlan(CurrentOptions());
            BuildWorkList(plan);
            SealMessage.Text = "等待开印";
            Root.UpdateLayout();
            SetProgress(0);
        }
        else
        {
            Fly.Opacity = 1;
            MoveFly(step, 0);
            if (step == 4)
            {
                ShowStampedState();
            }
        }

        Root.UpdateLayout();
    }

    // ============================================================
    // 动画小工具
    // ============================================================
    private static void Animate(
        IAnimatable target,
        DependencyProperty property,
        double from,
        double to,
        double durationMs,
        IEasingFunction? easing,
        TimeSpan? beginTime = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };

        // 只有真的要延迟时才碰 BeginTime。显式写 BeginTime = null 会让这条时间线永远不启动
        // （WPF 把「本地值为 null」当成「等父时间线」而不是「立即开始」），落印整条流程会静默失效。
        if (beginTime.HasValue)
        {
            animation.BeginTime = beginTime.Value;
        }

        target.BeginAnimation(property, animation);
    }

    private static void Snap(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        if (target is DependencyObject instance)
        {
            instance.SetValue(property, value);
        }
    }

    private static Task DelayAsync(double milliseconds) => Task.Delay(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>工作清单里的一行：做完就把墨浸过去。</summary>
    private sealed class WorkRow(StackPanel host, Rectangle tick, TextBlock title)
    {
        public void Done()
        {
            host.Opacity = 1;
            tick.Fill = (Brush)Application.Current.Resources["Brush.Primary"];
            tick.Stroke = (Brush)Application.Current.Resources["Brush.Primary"];
            title.Foreground = (Brush)Application.Current.Resources["Brush.TextSecondary"];
        }

        public void Fail()
        {
            host.Opacity = 1;
            tick.Stroke = (Brush)Application.Current.Resources["Brush.Error"];
            title.Foreground = (Brush)Application.Current.Resources["Brush.Error"];
        }
    }
}
