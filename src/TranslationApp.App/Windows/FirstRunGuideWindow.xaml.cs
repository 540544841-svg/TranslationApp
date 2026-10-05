using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Services;

namespace TranslationApp.Windows;

/// <summary>
/// 首启「立契」——设计稿 inkseal-motion-v2.html 的安装流程：一屏一个决定，
/// 启封 → 落址 → 接引擎 → 盖印 → 契成，途中一方印在进度脊与落款处之间飞。
///
/// 视觉照设计稿；数据全部接真实设置、真实引擎与真实自检，没有演示值。
/// 向导只读取/写入现有设置，失败项通过事件交回宿主打开 Doctor 或设置页。
/// </summary>
public partial class FirstRunGuideWindow : Window
{
    private static readonly string[] StepNames = ["启封", "落址", "接引擎", "盖印"];
    private static readonly string[] StepHints =
    [
        "契成之后，一切都还能在设置里改",
        "便携与常规安装随时可切",
        "密钥不出本机 · 不经中转",
        "开印后无需重启",
        "契已成立 · 印已落",
    ];

    // ---- 缓动曲线：与设计稿 CSS cubic-bezier 数值一一对应 ----
    private static readonly KeySpline EaseStandard = new(0.2, 0, 0, 1);
    private static readonly KeySpline EaseDec = new(0.05, 0.7, 0.1, 1);
    private static readonly KeySpline EaseAcc = new(0.3, 0, 0.8, 0.15);
    private static readonly KeySpline EaseStamp = new(0.34, 1.45, 0.5, 1);

    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly HotkeyManager _hotkeys;
    private readonly TranslatorCatalog _catalog;
    private readonly OcrService _ocr;
    private readonly AutoStart _autoStart;

    private readonly List<EngineChoice> _engines = [];
    private readonly List<SetupField> _fields = [];
    private readonly List<WorkRow> _work = [];
    private readonly List<Border> _ghostRings = [];

    private bool _hotkeysSuspended;
    private bool _suppressEngineChange;
    private bool _sealing;
    private int _step;
    private int _flyToken;
    private double _flyX;
    private double _flyY;
    private double _flyScale = 1;
    private int _checkFailures;

    public event EventHandler? OpenDoctorRequested;

    /// <summary>带分区名请求宿主打开设置（"热键" / "引擎"…）。</summary>
    public event EventHandler<string>? OpenSettingsRequested;

    public FirstRunGuideWindow(
        AppSettings settings,
        ISettingsStore store,
        HotkeyManager hotkeys,
        TranslatorCatalog catalog,
        OcrService ocr,
        InPlaceTranslationService translations,
        AutoStart autoStart)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _hotkeys = hotkeys;
        _catalog = catalog;
        _ocr = ocr;
        _autoStart = autoStart;

        // 向导在前台改热键之前先把全局热键收起来，避免占位判定把向导自己算进去
        _hotkeys.SuspendAll();
        _hotkeysSuspended = true;

        BuildEngines();
        BuildWorkList();
        BuildContractRows();
        BuildGhostRings();
        ApplyLocalFacts();
        UpdateReviewCards();

        Loaded += (_, _) =>
        {
            GoTo(0, instant: true);
        };
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!_hotkeysSuspended)
        {
            _hotkeys.SuspendAll();
            _hotkeysSuspended = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _flyToken++;
        if (_hotkeysSuspended)
        {
            _hotkeys.ResumeAll();
            _hotkeysSuspended = false;
        }

        base.OnClosed(e);
    }

    // ==================== 离屏出图 ====================

    /// <summary>
    /// 任意一步的静止帧：给离屏出图逐屏核对版面用，不跑任何动画。
    /// 盖印那一步顺手把清单勾完、进度推满，否则拍到的是还没开印的空态。
    /// </summary>
    public void ShowStepFrame(int step)
    {
        GoTo(step, instant: true);
        if (step != 3)
        {
            return;
        }

        foreach (var row in _work)
        {
            row.SetDone(true, row.PendingNote);
        }

        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth);
        SealMessage.Text = "印已落 · 契成";
    }

    /// <summary>契成页静止帧：印已落、清单已勾完、进度条已满。</summary>
    public void ShowFinalFrame()
    {
        GoTo(3, instant: true);
        foreach (var row in _work)
        {
            row.SetDone(true, row.PendingNote);
        }

        ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth);
        SealMessage.Text = "印已落 · 契成";
        GoTo(4, instant: true);
    }

    // ==================== 骨架 ====================

    private void BuildEngines()
    {
        foreach (var engine in _catalog.All)
        {
            var (display, badge, needsKey) = engine.Id switch
            {
                "bing" => ("Bing", "免密钥", false),
                "google" => ("Google", "免密钥", false),
                "tencent" => ("腾讯云 TMT", "需密钥", true),
                "baidu" => ("百度翻译", "需密钥", true),
                "azure" => ("Azure", "需密钥", true),
                "deepl" => ("DeepL", "需密钥", true),
                "llm" => ("AI 引擎", "兼容", true),
                _ => (engine.Name, "", true),
            };
            _engines.Add(new EngineChoice(engine.Id, engine.Name, display, badge, needsKey));
        }

        EngineList.ItemsSource = _engines;
        _suppressEngineChange = true;
        EngineList.SelectedItem = _engines.FirstOrDefault(item => item.Id == _settings.Engine) ?? _engines[0];
        _suppressEngineChange = false;
        // 初始化时 SelectedItem 的变更被上面那道闒拦下了，右侧栏得手动建一次，
        // 否则「接引擎」一进来右边永远是空的。
        RebuildEngineSide();
    }

    private void BuildWorkList()
    {
        AddWork("校验可执行文件与数据目录", () =>
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !File.Exists(exe))
            {
                return (false, "没找到当前可执行文件");
            }

            var mb = new FileInfo(exe).Length / 1024d / 1024d;
            return (true, $"{mb:F0} MB · 单文件");
        });

        AddWork("确认落址可写", () =>
        {
            if (!AppPaths.CanWriteBaseDirectory && AppPaths.IsPortable)
            {
                return (false, "程序目录不可写");
            }

            return (true, AppPaths.IsPortable ? "便携 · 随盘切换" : "用户目录 · 常规");
        });

        AddWork("注册全局热键", () =>
        {
            var missing = CoreHotkeys().Where(pair => !_hotkeys.IsRegistered(pair.Name)).ToArray();
            return missing.Length == 0
                ? (true, PrimaryHotkey())
                : (false, $"{missing.Length} 个未注册");
        });

        AddWork("写入引擎配置", () =>
        {
            var engine = _catalog.Find(_settings.Engine);
            if (engine is null)
            {
                return (false, "引擎不存在");
            }

            return engine.IsConfigured
                ? (true, engine.Id is "bing" or "google" ? "免密钥" : "DPAPI 加密落盘")
                : (false, "密钥未就绪");
        });

        AddWork("自检引擎 · 热键 · OCR", () =>
        {
            _checkFailures = 0;
            var engineOk = _catalog.Resolve(_settings.Engine).IsConfigured;
            var hotkeyOk = CoreHotkeys().All(pair =>
                HotkeyDefinition.TryParse(pair.Value, out _) &&
                _hotkeys.IsRegistered(pair.Name));
            var ocrOk = _ocr.IsAvailable;
            _checkFailures = new[] { engineOk, hotkeyOk, ocrOk }.Count(ok => !ok);

            if (_checkFailures == 0)
            {
                return (true, $"引擎 · 热键 · OCR 均就绪（OCR {_ocr.AvailableLanguages.Count} 个语言包）");
            }

            var parts = new List<string>();
            if (!engineOk)
            {
                parts.Add("引擎未配置");
            }

            if (!hotkeyOk)
            {
                parts.Add("热键未全部注册");
            }

            if (!ocrOk)
            {
                parts.Add("缺 OCR 语言包");
            }

            return (false, string.Join("；", parts));
        });
    }

    private void AddWork(string title, Func<(bool Ok, string Note)> run)
    {
        var tick = new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 3,
            RadiusY = 3,
            StrokeThickness = 1,
            Stroke = (Brush)FindResource("Brush.BorderStrong"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(45),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = title,
            Margin = new Thickness(10, 0, 0, 0),
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Brush.TextTertiary"),
        };
        var note = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Foreground = (Brush)FindResource("Brush.TextTertiary"),
        };

        var host = new Grid { Opacity = 0.5, Margin = new Thickness(0, 1, 0, 1) };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(tick, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(note, 2);
        host.Children.Add(tick);
        host.Children.Add(label);
        host.Children.Add(note);
        WorkList.Children.Add(host);

        _work.Add(new WorkRow(host, tick, label, note, run));
    }

    private void BuildContractRows()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        AddContractRow("版 本", $"{version} · 单文件 · 免安装");
        AddContractRow("落 址", AppPaths.BaseDirectory);
        AddContractRow("引 擎", EngineSummary());
        AddContractRow("热 键", $"{_settings.HotkeyInputTranslate} 呼出 · {_settings.HotkeySelectTranslate} 划词 · {_settings.HotkeyCaptureTranslate} 截图");
        AddContractRow("自 检", "引擎 · 热键 · OCR · 启动即跑");
        AddContractRow("立 契", "本机 · 当前用户");
        AddContractRow("留 痕", "只落在本机，不上传");
    }

    private void AddContractRow(string label, string value)
    {
        // 设计稿 .contract 的 flex gap:8px：单行 12.2px 文字 + 8px 行距，与网页版同节奏。
        var row = new Grid { Margin = new Thickness(0, 0, 96, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Brush.TextTertiary"),
        };
        var text = new TextBlock
        {
            Text = value,
            Margin = new Thickness(14, 0, 0, 0),
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("Brush.TextSecondary"),
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        ContractRows.Children.Add(row);
    }

    private void BuildGhostRings()
    {
        var vermilion = (Brush)FindResource("Brush.Primary");
        for (var i = 0; i < 3; i++)
        {
            var ring = new Border
            {
                Width = 60,
                Height = 60,
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1.5),
                BorderBrush = vermilion,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.5, 0.5),
            };
            _ghostRings.Add(ring);
            GhostRings.Children.Add(ring);
        }
    }

    private void ApplyLocalFacts()
    {
        FactHotkey.Text = _settings.HotkeyInputTranslate;
        InstallPathText.Text = AppPaths.BaseDirectory;
        WhyText.Text = "开机后只驻托盘、不占窗口，"
            + $"{_settings.HotkeyInputTranslate} 随时呼出；不需要可随时关掉。"
            + "印谱落址自动判断：装在固定盘写用户目录，装进 U 盘就跟着程序走。";
        SizeText.Text = ExeSizeText();
        PortableText.Text = AppPaths.IsPortable ? "便携 · 随盘切换" : "常规 · 用户目录";
        AutoStartToggle.IsChecked = SafeAutoStartEnabled();
        UpdateReviewCards();
    }

    private static string ExeSizeText()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            return "—";
        }

        var megabytes = new FileInfo(exe).Length / 1024d / 1024d;
        return megabytes < 10 ? $"{megabytes:F1} MB" : $"{megabytes:F0} MB";
    }

    private bool SafeAutoStartEnabled()
    {
        try
        {
            return _autoStart.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ==================== 状态更新 ====================

    private (string Name, string Value)[] CoreHotkeys() =>
    [
        ("input", _settings.HotkeyInputTranslate),
        ("select", _settings.HotkeySelectTranslate),
        ("capture", _settings.HotkeyCaptureTranslate),
    ];

    private string PrimaryHotkey() => _settings.HotkeyInputTranslate;

    private string EngineSummary()
    {
        var choice = SelectedEngine();
        return choice is null ? _settings.Engine : $"{choice.Display} · {choice.BadgeText}";
    }

    private EngineChoice? SelectedEngine() => EngineList.SelectedItem as EngineChoice;

    private void UpdateReviewCards()
    {
        RevPath.Text = AppPaths.BaseDirectory;
        RevEngine.Text = EngineSummary();
        RevHotkey.Text = PrimaryHotkey();
        RevResident.Text = SafeAutoStartEnabled() ? "仅托盘" : "不常驻";
        HintText.Text = StepHints[Math.Clamp(_step, 0, 4)];
    }

    private void UpdateRail(int index)
    {
        var dots = new[] { SlotDot0, SlotDot1, SlotDot2, SlotDot3 };
        var halos = new[] { SlotHalo0, SlotHalo1, SlotHalo2, SlotHalo3 };
        var texts = new[] { SlotText0, SlotText1, SlotText2, SlotText3 };
        var segs = new[] { Seg0, Seg1, Seg2, Seg3 };
        var primary = (Brush)FindResource("Brush.Primary");
        var strong = (Brush)FindResource("Brush.BorderStrong");
        var tertiary = (Brush)FindResource("Brush.TextTertiary");
        var primaryText = (Brush)FindResource("Brush.TextPrimary");
        var semibold = (FontWeight)FindResource("Weight.Semibold");

        for (var i = 0; i < 4; i++)
        {
            var done = i < index;
            var on = i == index;
            dots[i].StrokeDashArray = on || done ? null : new DoubleCollection { 1.6, 1.2 };
            dots[i].Stroke = on || done ? primary : strong;
            dots[i].Fill = done ? primary : Brushes.Transparent;
            halos[i].Opacity = on ? 1 : 0;
            texts[i].Foreground = on ? primaryText : tertiary;
            texts[i].FontWeight = on ? semibold : FontWeights.Normal;

            var segDone = i < index;
            segs[i].Background = segDone ? primary : strong;
            Animate(segs[i], UIElement.OpacityProperty, segDone ? 1 : 0.42, 300, EaseDec);
        }
    }

    private void GoTo(int index, bool instant = false)
    {
        index = Math.Clamp(index, 0, 4);
        _step = index;

        Pane0.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Pane1.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Pane2.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        Pane3.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        Pane4.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        Root.UpdateLayout();

        UpdateRail(index);
        StepText.Text = index < 4 ? $"第 {index + 1} / 4 步 · {StepNames[index]}" : "契已成立";
        HintText.Text = StepHints[index];
        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = index switch
        {
            3 => "落印",
            4 => "启印",
            _ => "继续",
        };
        UpdateReviewCards();
        SetBusy(false);

        if (!instant)
        {
            FadeIn(CurrentPane());
        }
        else
        {
            CurrentPane().Opacity = 1;
        }

        if (index == 4)
        {
            if (instant)
            {
                Fly.Opacity = 0;
                SignSpot.Opacity = 0;
                SignSpotText.Opacity = 0;
                GhostSeal.Opacity = 1;
            }
            else
            {
                PlaceFly(3, animate: false, 0);
                _ = FlyToContractAsync();
            }
        }
        else
        {
            PlaceFly(index, animate: !instant, index == 0 ? 400 : 340);
        }
    }

    private FrameworkElement CurrentPane() => _step switch
    {
        0 => Pane0,
        1 => Pane1,
        2 => Pane2,
        3 => Pane3,
        _ => Pane4,
    };

    private static void FadeIn(FrameworkElement pane)
    {
        pane.Opacity = 0;
        var move = new TranslateTransform(0, -6);
        pane.RenderTransform = move;
        Animate(pane, UIElement.OpacityProperty, 1, 300, EaseDec);
        Animate(move, TranslateTransform.YProperty, 0, 300, EaseDec);
    }

    private void SetBusy(bool busy)
    {
        _sealing = busy;
        NextButton.IsEnabled = !busy;
        BackButton.IsEnabled = !busy;
        UnsealButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    // ==================== 飞印 ====================

    private (double X, double Y, double Scale) FlyTarget(int step)
    {
        FrameworkElement anchor;
        double size;
        if (step == 0)
        {
            anchor = AnchorWelcome;
            size = 64;
        }
        else if (step == 4)
        {
            anchor = SignSpot;
            size = 66;
        }
        else
        {
            anchor = new[] { SlotDot0, SlotDot1, SlotDot2, SlotDot3 }[step - 1];
            size = 15;
        }

        // 以 Root（窗口内容根）为锚点：出图时 Content 会被脱下来另挂宿主，那时它已不在窗口树下。
        var center = anchor.TransformToAncestor(Root)
            .Transform(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
        return (center.X - 32, center.Y - 32, size / 64);
    }

    private void PlaceFly(int step, bool animate, int milliseconds)
    {
        var (x, y, scale) = FlyTarget(step);
        _flyX = x;
        _flyY = y;
        _flyScale = scale;
        Fly.Opacity = 1;
        FlyRotate.Angle = 0;

        if (!animate)
        {
            SetNow(FlyMove, TranslateTransform.XProperty, x);
            SetNow(FlyMove, TranslateTransform.YProperty, y);
            SetNow(FlyScale, ScaleTransform.ScaleXProperty, scale);
            SetNow(FlyScale, ScaleTransform.ScaleYProperty, scale);
            return;
        }

        Animate(FlyMove, TranslateTransform.XProperty, x, milliseconds, EaseDec);
        Animate(FlyMove, TranslateTransform.YProperty, y, milliseconds, EaseDec);
        Animate(FlyScale, ScaleTransform.ScaleXProperty, scale, milliseconds, EaseDec);
        Animate(FlyScale, ScaleTransform.ScaleYProperty, scale, milliseconds, EaseDec);
    }

    private async Task FlyToContractAsync()
    {
        var token = ++_flyToken;
        await Task.Delay(350);
        if (token != _flyToken)
        {
            return;
        }

        PlaceFly(4, animate: true, 400);
        await Task.Delay(430);
        if (token != _flyToken)
        {
            return;
        }

        await StampAsync(token);
    }

    /// <summary>落印：先抬 26px、转 -8°，再砸下、挤压、激起震荡波，印影留在落款处。</summary>
    private async Task StampAsync(int token)
    {
        var x = _flyX;
        var y = _flyY;
        var scale = _flyScale;
        _ = x;

        Animate(FlyMove, TranslateTransform.YProperty, y - 26, 120, EaseStandard);
        Animate(FlyRotate, RotateTransform.AngleProperty, -8, 120, EaseStandard);
        await Task.Delay(130);
        if (token != _flyToken)
        {
            return;
        }

        Animate(FlyMove, TranslateTransform.YProperty, y, 110, EaseAcc);
        Animate(FlyRotate, RotateTransform.AngleProperty, 0, 110, EaseAcc);
        await Task.Delay(110);
        if (token != _flyToken)
        {
            return;
        }

        SetNow(FlyMove, TranslateTransform.YProperty, y);
        SetNow(FlyScale, ScaleTransform.ScaleXProperty, scale * 1.28);
        SetNow(FlyScale, ScaleTransform.ScaleYProperty, scale * 0.70);
        ImpactRings();
        Animate(SignSpot, UIElement.OpacityProperty, 0, 180, EaseDec);
        Animate(SignSpotText, UIElement.OpacityProperty, 0, 180, EaseDec);
        Animate(GhostSeal, UIElement.OpacityProperty, 1, 200, EaseDec);
        Animate(FlyScale, ScaleTransform.ScaleXProperty, scale, 210, EaseStamp);
        Animate(FlyScale, ScaleTransform.ScaleYProperty, scale, 210, EaseStamp);
        HintText.Text = "契已成立 · 印已落";

        await Task.Delay(260);
        if (token != _flyToken)
        {
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Fly.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void ImpactRings()
    {
        for (var i = 0; i < _ghostRings.Count; i++)
        {
            var ring = _ghostRings[i];
            var scale = (ScaleTransform)ring.RenderTransform;
            SetNow(ring, UIElement.OpacityProperty, 0.8);
            SetNow(scale, ScaleTransform.ScaleXProperty, 0.5);
            SetNow(scale, ScaleTransform.ScaleYProperty, 0.5);
            Animate(scale, ScaleTransform.ScaleXProperty, 1.9, 420, EaseDec, i * 46, baseValue: 0.5);
            Animate(scale, ScaleTransform.ScaleYProperty, 1.9, 420, EaseDec, i * 46, baseValue: 0.5);
            Animate(ring, UIElement.OpacityProperty, 0, 400, null, i * 46, baseValue: 0.8);
        }
    }

    // ==================== 交互 ====================

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_sealing || _step == 0)
        {
            return;
        }

        GoTo(_step - 1);
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_sealing)
        {
            return;
        }

        switch (_step)
        {
            case 0:
                GoTo(1);
                return;
            case 1:
                ApplyAutoStart();
                _store.Save(_settings);
                GoTo(2);
                return;
            case 2:
                if (!TryCommitEngine())
                {
                    return;
                }

                GoTo(3);
                return;
            case 3:
                await RunSealAsync();
                return;
            default:
                Close();
                return;
        }
    }

    private void ApplyAutoStart()
    {
        var wanted = AutoStartToggle.IsChecked == true;
        try
        {
            _autoStart.SetEnabled(wanted);
        }
        catch (Exception)
        {
            // 关不掉/开不上都不该挡住立契：真实状态以系统为准，核对格下一拍会照实显示
        }

        UpdateReviewCards();
    }

    /// <summary>
    /// 离开「接引擎」时落盘：字段写回设置，密钥走 DPAPI；
    /// 只有确认真能用的引擎才切过去——没填密钥就保持原引擎，不让一次选择把翻译打断。
    /// </summary>
    private bool TryCommitEngine()
    {
        PersistFields();
        _store.Save(_settings);

        var choice = SelectedEngine();
        if (choice is null)
        {
            return true;
        }

        var engine = _catalog.Find(choice.Id);
        if (engine is { IsConfigured: true })
        {
            _settings.Engine = choice.Id;
            _store.Save(_settings);
            return true;
        }

        TestMessage.Text = $"{choice.Display} 还不能用：先补齐上面的密钥，再点「测试连接」。";
        TestMessage.Foreground = (Brush)FindResource("Brush.Warning");
        UpdateReviewCards();
        return true;
    }

    private async Task RunSealAsync()
    {
        SetBusy(true);
        foreach (var row in _work)
        {
            row.Reset();
        }

        SealMessage.Text = "正在开印…";
        SealMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");
        FixHotkeyButton.Visibility = Visibility.Collapsed;
        OpenDoctorButton.Visibility = Visibility.Collapsed;

        var failures = 0;
        for (var i = 0; i < _work.Count; i++)
        {
            await Task.Delay(110);
            var (ok, note) = _work[i].Run();
            _work[i].SetDone(ok, note);
            if (!ok)
            {
                failures++;
            }

            Animate(ProgressFill, FrameworkElement.WidthProperty,
                ProgressTrack.ActualWidth * (i + 1) / _work.Count, 300, EaseDec);
            await Task.Delay(190);
        }

        if (failures == 0)
        {
            SealMessage.Text = "印已落 · 契成";
            SealMessage.Foreground = (Brush)FindResource("Brush.Success");
        }
        else
        {
            SealMessage.Text = $"{failures} 项待处理 · 不挡住使用，可稍后在设置里补";
            SealMessage.Foreground = (Brush)FindResource("Brush.Warning");
            FixHotkeyButton.Visibility = _checkFailures > 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenDoctorButton.Visibility = Visibility.Visible;
        }

        SetBusy(false);
        GoTo(4);
    }

    private void OnFixHotkeyClick(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested?.Invoke(this, "热键");
        Close();
    }

    private void OnOpenDoctorClick(object sender, RoutedEventArgs e)
    {
        OpenDoctorRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.BaseDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 打不开资源管理器不影响立契
        }
    }

    private void OnEngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEngineChange)
        {
            return;
        }

        RebuildEngineSide();
    }

    private async void OnTestEngineClick(object sender, RoutedEventArgs e)
    {
        var choice = SelectedEngine();
        if (choice is null)
        {
            return;
        }

        PersistFields();
        _store.Save(_settings);

        var engine = _catalog.Find(choice.Id);
        if (engine is not { IsConfigured: true })
        {
            TestMessage.Text = "还不能用：先补齐上面的密钥。";
            TestMessage.Foreground = (Brush)FindResource("Brush.Warning");
            return;
        }

        TestButton.IsEnabled = false;
        TestMessage.Text = "正在连接…";
        TestMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");
        try
        {
            var started = Environment.TickCount64;
            var result = await engine.TranslateAsync("hello", "en", "zh-CN");
            var elapsed = Environment.TickCount64 - started;
            TestMessage.Text = $"连接正常 · {elapsed} ms · {result.TranslatedText}";
            TestMessage.Foreground = (Brush)FindResource("Brush.Success");

            _settings.Engine = choice.Id;
            _store.Save(_settings);
            UpdateReviewCards();
        }
        catch (Exception ex)
        {
            TestMessage.Text = $"连接失败：{ex.Message}";
            TestMessage.Foreground = (Brush)FindResource("Brush.Error");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ==================== 引擎侧栏 ====================

    private static readonly Dictionary<string, FieldSpec[]> EngineSchema = new()
    {
        ["tencent"] =
        [
            new("SecretId", false, s => s.TencentSecretId, (s, v) => s.TencentSecretId = v.Trim()),
            new("SecretKey", true,
                s => SecretStore.Unprotect(s.TencentSecretKeyEncrypted) ?? "",
                (s, v) => s.TencentSecretKeyEncrypted = SecretStore.Protect(v)),
        ],
        ["baidu"] =
        [
            new("APPID", false, s => s.BaiduAppId, (s, v) => s.BaiduAppId = v.Trim()),
            new("密钥", true,
                s => SecretStore.Unprotect(s.BaiduAppKeyEncrypted) ?? "",
                (s, v) => s.BaiduAppKeyEncrypted = SecretStore.Protect(v)),
        ],
        ["azure"] =
        [
            new("订阅密钥", true,
                s => SecretStore.Unprotect(s.AzureSubscriptionKeyEncrypted) ?? "",
                (s, v) => s.AzureSubscriptionKeyEncrypted = SecretStore.Protect(v)),
            new("区域（单服务资源可留空）", false, s => s.AzureRegion, (s, v) => s.AzureRegion = v.Trim()),
        ],
        ["deepl"] =
        [
            new("Authentication Key", true,
                s => SecretStore.Unprotect(s.DeepLApiKeyEncrypted) ?? "",
                (s, v) => s.DeepLApiKeyEncrypted = SecretStore.Protect(v)),
        ],
        ["llm"] =
        [
            // 0.3.2 起 AI 是多档供应商：向导只编辑「当前档」，多档与协议在「设置 → 引擎」里调
            new("接口地址", false,
                s => s.ActiveLlmProvider?.BaseUrl ?? "",
                (s, v) => { if (s.ActiveLlmProvider is { } p) { p.BaseUrl = v.Trim(); } }),
            new("API Key", true,
                s => SecretStore.Unprotect(s.ActiveLlmProvider?.ApiKeyEncrypted) ?? "",
                (s, v) => { if (s.ActiveLlmProvider is { } p) { p.ApiKeyEncrypted = SecretStore.Protect(v); } }),
            new("模型名", false,
                s => s.ActiveLlmProvider?.Model ?? "",
                (s, v) => { if (s.ActiveLlmProvider is { } p) { p.Model = v.Trim(); } }),
        ],
    };

    private void RebuildEngineSide()
    {
        var choice = SelectedEngine();
        foreach (var field in _fields)
        {
            field.Write(_settings, field.Value);
        }

        _fields.Clear();
        EngineFields.Children.Clear();
        EngineRows.Children.Clear();
        TestMessage.Text = "";
        TestMessage.Foreground = (Brush)FindResource("Brush.TextTertiary");

        if (choice is null)
        {
            return;
        }

        EngineNote.Text = choice.Id switch
        {
            "bing" => "Bing 免密钥，开箱可用，不需要任何配置。",
            "google" => "Google 免密钥，但国内网络通常要走代理才连得上。",
            "tencent" => "需要 SecretId 与 SecretKey——在腾讯云控制台建一个机器翻译应用，把两串密钥粘进来。",
            "baidu" => "需要 APPID 与密钥——在百度翻译开放平台申请通用翻译 API 之后粘贴。",
            "azure" => "需要订阅密钥；区域如 eastasia，只有「多服务资源」才需要填。",
            "deepl" => "需要 API Key——Free 与 Pro 都能用，Key 以 :fx 结尾的是免费版。",
            "llm" => "需要 OpenAI 兼容的接口地址与 Key——可以接自建模型或 DeepSeek。",
            _ => "随时可在「设置 → 引擎」里换。",
        };

        AddSideRow("调用方式", choice.NeedsKey ? "HTTPS · 需鉴权" : "HTTPS · 免登录");
        AddSideRow("密钥存储", choice.NeedsKey ? "DPAPI 加密落盘" : "不使用密钥");

        if (EngineSchema.TryGetValue(choice.Id, out var specs))
        {
            foreach (var spec in specs)
            {
                EngineFields.Children.Add(BuildFieldRow(spec));
            }
        }
    }

    private void AddSideRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Brush.TextTertiary"),
        };
        var text = new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Right,
            Foreground = (Brush)FindResource("Brush.TextSecondary"),
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        EngineRows.Children.Add(row);
    }

    private FrameworkElement BuildFieldRow(FieldSpec spec)
    {
        var label = new TextBlock
        {
            Text = spec.Secret ? $"{spec.Label} · 只存本机" : spec.Label,
            Style = (Style)FindResource("Setup.Label"),
            Margin = new Thickness(0, 0, 0, 5),
        };
        FrameworkElement input;
        TextBox? box = null;
        PasswordBox? secret = null;
        if (spec.Secret)
        {
            secret = new PasswordBox
            {
                Style = (Style)FindResource("Field.PasswordBox"),
                Password = spec.Read(_settings),
            };
            input = secret;
        }
        else
        {
            box = new TextBox
            {
                Style = (Style)FindResource("Field.TextBox"),
                Text = spec.Read(_settings),
            };
            input = box;
        }

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(label);
        panel.Children.Add(input);
        _fields.Add(new SetupField(box, secret, spec.Write));
        return panel;
    }

    private void PersistFields()
    {
        foreach (var field in _fields)
        {
            field.Write(_settings, field.Value);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!_sealing && _step > 0)
            {
                GoTo(_step - 1);
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return && !IsTextEntryFocused())
        {
            OnNextClick(NextButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private bool IsTextEntryFocused() =>
        Keyboard.FocusedElement is TextBox or PasswordBox;

    // ==================== 动画工具 ====================

    private static void SetNow(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        if (target is DependencyObject obj)
        {
            obj.SetValue(property, value);
        }
    }

    /// <summary>从当前值补到目标值；接力时先读当前值当起点，换动画的那一帧不会跳回基值。</summary>
    private static void Animate(
        IAnimatable target,
        DependencyProperty property,
        double to,
        int milliseconds,
        KeySpline? spline,
        double delayMs = 0,
        double? baseValue = null)
    {
        var from = baseValue
            ?? (target is DependencyObject current && current.GetValue(property) is double value ? value : to);
        target.BeginAnimation(property, null);
        if (target is DependencyObject obj)
        {
            obj.SetValue(property, from);
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.HoldEnd,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(spline is null
            ? new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)))
            : new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), spline));
        target.BeginAnimation(property, animation);
    }

    // ==================== 小模型 ====================

    private sealed record EngineChoice(string Id, string Name, string Display, string BadgeText, bool NeedsKey);

    private sealed record FieldSpec(
        string Label,
        bool Secret,
        Func<AppSettings, string> Read,
        Action<AppSettings, string> Write);

    private sealed record SetupField(TextBox? Box, PasswordBox? Secret, Action<AppSettings, string> Write)
    {
        public string Value => Box?.Text ?? Secret?.Password ?? "";
    }

    private sealed class WorkRow(
        Grid host,
        Rectangle tick,
        TextBlock label,
        TextBlock note,
        Func<(bool Ok, string Note)> run)
    {
        private readonly Brush _ok = (Brush)host.FindResource("Brush.Primary");
        private readonly Brush _bad = (Brush)host.FindResource("Brush.Warning");
        private readonly Brush _idle = (Brush)host.FindResource("Brush.BorderStrong");
        private readonly Brush _text = (Brush)host.FindResource("Brush.TextSecondary");
        private readonly Brush _dim = (Brush)host.FindResource("Brush.TextTertiary");

        public string PendingNote { get; private set; } = "";

        public (bool Ok, string Note) Run() => run();

        public void Reset()
        {
            host.Opacity = 0.5;
            tick.Fill = Brushes.Transparent;
            tick.Stroke = _idle;
            tick.StrokeDashArray = new DoubleCollection { 1.6, 1.2 };
            label.Foreground = _dim;
            note.Text = "";
        }

        public void SetDone(bool ok, string detail)
        {
            PendingNote = detail;
            host.Opacity = 1;
            tick.StrokeDashArray = null;
            tick.Stroke = ok ? _ok : _bad;
            tick.Fill = ok ? _ok : Brushes.Transparent;
            label.Foreground = _text;
            note.Text = detail;
            note.Foreground = ok ? _dim : _bad;
        }
    }
}
