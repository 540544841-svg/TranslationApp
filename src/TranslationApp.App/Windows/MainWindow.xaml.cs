using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TranslationApp.Controls;
using TranslationApp.Core.Hotkey;
using TranslationApp.Theming;
using TranslationApp.ViewModels;

namespace TranslationApp.Windows;

/// <summary>设置主窗口（FR-009）：关闭按钮=隐藏，托盘常驻应用复用同一实例。</summary>
public partial class MainWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly HotkeyManager _hotkeys;
    private LaunchRevealView? _revealLayer;
    private bool _hotkeysSuspended;

    public MainWindow(SettingsViewModel viewModel, HotkeyManager hotkeys)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _hotkeys = hotkeys;
        _viewModel.SettingsNavigationRequested += (_, section) => NavigateToSection(section);
        DataContext = viewModel;
        InstallLaunchRevealLayer();
        CommandBindings.Add(new CommandBinding(
            // 最小化与关闭同样默认回托盘：译印是常驻件，任务栏上不该多一个按钮。
            SystemCommands.MinimizeWindowCommand, (_, _) => Hide()));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand, (_, _) => WindowState = WindowState.Maximized));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.RestoreWindowCommand, (_, _) => WindowState = WindowState.Normal));
        CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => Hide()));

        // 代理密码框不是可绑定属性（WPF 出于安全不暴露 Password 为依赖属性），
        // 因此回填已保存的密码并在此把输入写回 ViewModel
        ProxyPasswordBox.Password = viewModel.ProxyPassword ?? "";
    }

    // ==================== 首启「启印」层 ====================

    /// <summary>
    /// 把窗口内容（导航 TabControl）挪进一层根 Grid，再在上面挂「启印」层。
    /// 这样不必改动 MainWindow.xaml 那三千多行的缩进，就能让启印铺满整窗。
    /// </summary>
    private void InstallLaunchRevealLayer()
    {
        var content = (FrameworkElement)Content;
        Content = null;

        _revealLayer = new LaunchRevealView { Visibility = Visibility.Collapsed };
        _revealLayer.Finished += (_, _) => HideLaunchReveal();
        // 点一下或按 Esc 就跳过：动画不该把用户困在启动画面上。
        _revealLayer.MouseLeftButtonDown += (_, _) => _revealLayer.Skip();
        PreviewKeyDown += OnRevealKeyDown;

        var root = new Grid();
        root.Children.Add(content);
        root.Children.Add(_revealLayer);
        Content = root;
    }

    private void OnRevealKeyDown(object sender, KeyEventArgs e)
    {
        if (_revealLayer is { Visibility: Visibility.Visible } && e.Key == Key.Escape)
        {
            e.Handled = true;
            _revealLayer.Skip();
        }
    }

    /// <summary>
    /// 把「启印」铺满整窗播一遍。系统关了动画效果、或已经播过时返回 false，
    /// 宿主应当当作没这回事（直接让人用界面）。
    /// </summary>
    public bool PlayLaunchReveal()
    {
        if (_revealLayer is null || !LaunchRevealView.CanPlay)
        {
            return false;
        }

        _revealLayer.Visibility = Visibility.Visible;
        _revealLayer.Play();
        return true;
    }

    /// <summary>
    /// 先把「启印」层铺上并置为不透明，**必须在 Window.Show() 之前调用**：窗口一变成可见，
    /// 合成线程就会抓走一帧，那一刻启印层若还是 Collapsed，用户先看到的就是工作台（会闪一下）。
    /// 只铺不播——时间轴等 PlayLaunchReveal 再起，所以开场那几拍不会丢。
    /// 系统关了动画效果时返回 false，宿主照常开窗即可。
    /// </summary>
    public bool PrimeLaunchReveal()
    {
        if (_revealLayer is null || !LaunchRevealView.CanPlay)
        {
            return false;
        }

        _revealLayer.Visibility = Visibility.Visible;
        _revealLayer.Prime();
        return true;
    }

    private void HideLaunchReveal()
    {
        if (_revealLayer is null)
        {
            return;
        }

        _revealLayer.Stop();
        _revealLayer.Visibility = Visibility.Collapsed;
        RevealFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启印层收掉时触发，宿主据此接上后续（立契）。</summary>
    public event EventHandler? RevealFinished;

    /// <summary>离屏出图用：把启印层摆成末帧并显示，用于核对它与工作台的层叠、尺寸与遮挡范围。</summary>
    public void ShowLaunchRevealFinalFrame()
    {
        if (_revealLayer is null)
        {
            return;
        }

        _revealLayer.Visibility = Visibility.Visible;
        _revealLayer.ShowFinalFrame();
    }

    private void OnWorkbenchInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return))
        {
            return;
        }

        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            return; // Shift+Enter 换行
        }

        e.Handled = true;
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            _viewModel.Workbench.TranslateCommand.Execute(null);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 原生标题栏需单独适配深色，否则深色主题下会出现白色标题栏
        ThemeManager.ApplyTitleBar(this);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 设置窗口录制热键时先释放全局注册，否则 Windows 会把现有组合吞成应用热键。
        if (!_hotkeysSuspended)
        {
            _hotkeys.SuspendAll();
            _hotkeysSuspended = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_hotkeysSuspended)
        {
            _hotkeys.ResumeAll();
            _hotkeysSuspended = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        if (_hotkeysSuspended)
        {
            _hotkeys.ResumeAll();
            _hotkeysSuspended = false;
        }
        Hide();
    }

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.ProxyPassword = box.Password;
        }
    }

    /// <summary>切到「引擎」页时刷新近 7 天成败看板（P0 批 1 / spec §4.3）。</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, NavTabs)) return;
        var header = (NavTabs.SelectedItem as TabItem)?.Header as string;
        if (header == "引擎")
        {
            _viewModel.RefreshEngineStats();
        }

        if (header == "工作台")
        {
            // 工作台有自己的摘要（今日印记 / 最近落印 / 引擎行），不跟印谱页的筛选走
            _ = _viewModel.RefreshWorkbenchAsync();
        }

        // 首次进入历史页才加载，避免启动设置窗口时就做一次 500 条查询
        if (header == "印谱" && _viewModel.HistoryCount == 0 && _viewModel.HistoryRows.Count == 0)
        {
            _ = _viewModel.RefreshHistoryOnceAsync();
        }
    }

    // ==================== 模型下拉：拖动排序（只针对自建条目） ====================

    /// <summary>一次按下最终要做什么：× 删除 / 拖排序 / 普通点选。</summary>
    private enum ModelRowGesture
    {
        None,
        Select,
        Delete,
        Reorder,
    }

    private ComboBox? _modelDragCombo;
    private FrameworkElement? _modelDragHost;
    private Point _modelDragOrigin;
    private string? _modelDragName;
    private ComboBoxItem? _modelDragRow;
    private AiProviderItemViewModel? _modelDragItem;
    private ModelRowGesture _modelGesture;
    private ComboBoxItem? _modelDropRow;
    private Border? _modelDropLine;
    private bool _modelDropAfter;
    private bool _modelDragging;
    private Popup? _modelGhost;
    private TextBlock? _modelGhostText;

    /// <summary>
    /// 弹层一开就接管它的鼠标。ComboBox 默认「按下即选中」，那会顺手把弹层收起来——拖拽起不来，
    /// 行尾 × 也点不成（高亮、落点、跟手纸片就都看不到）。这里改成自己处理：
    /// 没动过＝选中并收起，动了＝排序且弹层留着，按在 × 上＝删。
    /// </summary>
    private void OnModelDropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox combo
            || combo.Template.FindName("PART_Popup", combo) is not Popup popup
            || popup.Child is not FrameworkElement host)
        {
            return;
        }

        _modelDragCombo = combo;
        _modelDragHost = host;

        // 先摘后挂：万一上一次没走到 Closed，也不会挂出两份
        host.PreviewMouseLeftButtonDown -= OnModelPopupMouseDown;
        host.PreviewMouseLeftButtonDown += OnModelPopupMouseDown;
        host.PreviewMouseMove -= OnModelPopupMouseMove;
        host.PreviewMouseMove += OnModelPopupMouseMove;
        host.PreviewMouseLeftButtonUp -= OnModelPopupMouseUp;
        host.PreviewMouseLeftButtonUp += OnModelPopupMouseUp;
        host.LostMouseCapture -= OnModelPopupLostCapture;
        host.LostMouseCapture += OnModelPopupLostCapture;

        // 行是这一刻才建出来的：等布局完再按 CustomModels 决定哪几行露 ×
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncRowDeleteChips));
    }

    private void OnModelDropDownClosed(object sender, EventArgs e)
    {
        if (_modelDragHost is { } host)
        {
            host.PreviewMouseLeftButtonDown -= OnModelPopupMouseDown;
            host.PreviewMouseMove -= OnModelPopupMouseMove;
            host.PreviewMouseLeftButtonUp -= OnModelPopupMouseUp;
            host.LostMouseCapture -= OnModelPopupLostCapture;
        }

        ResetModelDrag();
        CloseModelGhost();
        _modelDragCombo = null;
        _modelDragHost = null;
    }

    /// <summary>行尾 × 只给自建条目：接口拉回的那份删不掉，也不该露这一枚。</summary>
    private void SyncRowDeleteChips()
    {
        if (_modelDragHost is not { } host
            || _modelDragCombo?.DataContext is not AiProviderCardViewModel card
            || card.Selected is not { } item)
        {
            return;
        }

        foreach (var row in FindRows(host).ToList())
        {
            row.ApplyTemplate();
            if (row.Template?.FindName("RowDelete", row) is not Border chip)
            {
                continue;
            }

            var custom = row.DataContext is string name
                && item.CustomModels.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
            chip.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnModelPopupMouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetModelDrag();

        if (RowFrom(e.OriginalSource) is not { } row || row.DataContext is not string name
            || _modelDragCombo?.DataContext is not AiProviderCardViewModel card || card.Selected is not { } item)
        {
            return;
        }

        // 拦下这次按下：不拦的话 ComboBox 当场选中这一行、弹层跟着收起来，拖不动也点不成 ×
        e.Handled = true;

        _modelDragRow = row;
        _modelDragItem = item;
        _modelDragName = name;
        _modelDragOrigin = e.GetPosition(this);
        _modelDragging = false;

        if (IsRowDelete(e.OriginalSource))
        {
            _modelGesture = ModelRowGesture.Delete;
            return;   // 删除不拖：不抢捕获，松手还在这一枚 × 上才算数
        }

        // 只有自建条目拖得动：接口拉回的那份不落盘，排了也存不住
        _modelGesture = item.CustomModels.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase))
            ? ModelRowGesture.Reorder
            : ModelRowGesture.Select;
        _modelDragHost?.CaptureMouse();
    }

    private void OnModelPopupMouseMove(object sender, MouseEventArgs e)
    {
        if (_modelGesture != ModelRowGesture.Reorder || _modelDragName is null
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (!_modelDragging)
        {
            var moved = e.GetPosition(this) - _modelDragOrigin;
            if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4)
            {
                return;   // 还当不上拖：多半只是想点一下
            }

            _modelDragging = true;
            if (_modelDragRow is { } row)
            {
                row.Opacity = 0.4;   // 原位留个淡影，说明这条正在被搬
            }

            OpenModelGhost();
        }

        UpdateModelDropTarget(e);
        MoveModelGhost();
    }

    private void OnModelPopupMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_modelDragName is not { } name || _modelGesture == ModelRowGesture.None)
        {
            return;
        }

        var item = _modelDragItem;
        var gesture = _modelGesture;
        var dropRow = _modelDropRow;
        var after = _modelDropAfter;
        var wasDragging = _modelDragging;

        // 这一下不还给 ComboBox：它一收弹层，拖拽就白做了
        e.Handled = true;
        ResetModelDrag();
        CloseModelGhost();

        if (gesture == ModelRowGesture.Delete)
        {
            // 松手还在这一枚 × 上才算删（中途拖开＝取消）
            if (item is not null && IsRowDelete(e.OriginalSource))
            {
                item.RemoveModelCommand.Execute(name);
                // 删完列表会重建一遍：× 得重新点一次名
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncRowDeleteChips));
            }

            return;
        }

        if (wasDragging)
        {
            if (item is not null && dropRow?.DataContext is string target)
            {
                item.MoveCustomModelNear(name, target, after);
            }

            return;
        }

        // 没动过：按普通点击走——选中这一条并收起弹层（按下那一下被我们拦了，得自己来）
        if (_modelDragCombo is { } combo && combo.DataContext is AiProviderCardViewModel card
            && card.Selected is { } current)
        {
            current.PickedModel = name;
            combo.SelectedItem = name;
            combo.IsDropDownOpen = false;
        }
    }

    /// <summary>拖动中途丢了鼠标捕获（Alt+Tab 之类）：把状态清干净，别留下一条一直淡着的行。</summary>
    private void OnModelPopupLostCapture(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            return;   // 手还按着：可能只是弹层重拿捕获，留着状态等 MouseUp
        }

        ResetModelDrag();
    }

    private void ResetModelDrag()
    {
        ClearModelDropLine();
        if (_modelDragRow is { } row)
        {
            row.Opacity = 1;
        }

        _modelDragHost?.ReleaseMouseCapture();
        _modelDragRow = null;
        _modelDragItem = null;
        _modelDragName = null;
        _modelDropRow = null;
        _modelDropAfter = false;
        _modelGesture = ModelRowGesture.None;
        _modelDragging = false;
    }

    /// <summary>弹层里那几行（下拉项本身就是 ComboBoxItem，直接收上来）。</summary>
    private static IEnumerable<ComboBoxItem> FindRows(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBoxItem row)
            {
                yield return row;
                continue;
            }

            foreach (var nested in FindRows(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>这一下是不是落在行尾的 × 上（模板里那枚 Border 叫 RowDelete）。</summary>
    private static bool IsRowDelete(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "RowDelete" })
            {
                return true;
            }

            if (node is ComboBoxItem)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>把插入线挪到光标底下那一行的上缘或下缘——线画在哪，松手就落在哪。</summary>
    private void UpdateModelDropTarget(MouseEventArgs e)
    {
        if (_modelDragHost is not { } host)
        {
            return;
        }

        var point = e.GetPosition(host);
        var row = RowUnderPoint(host, point);
        var after = row is not null && point.Y > row.TranslatePoint(new Point(0, row.ActualHeight / 2), host).Y;

        if (ReferenceEquals(row, _modelDropRow) && after == _modelDropAfter)
        {
            return;
        }

        ClearModelDropLine();
        _modelDropRow = row;
        _modelDropAfter = after;
        if (row is null || _modelDragName is null || row.DataContext is not string target
            || string.Equals(target, _modelDragName, StringComparison.OrdinalIgnoreCase))
        {
            return;   // 拖到自己那一格：没有可落的地方，不画线
        }

        row.ApplyTemplate();
        if (row.Template?.FindName(after ? "DropAfter" : "DropBefore", row) is Border line)
        {
            _modelDropLine = line;
            line.Visibility = Visibility.Visible;
        }
    }

    private void ClearModelDropLine()
    {
        if (_modelDropLine is null)
        {
            return;
        }

        _modelDropLine.Visibility = Visibility.Collapsed;
        _modelDropLine = null;
    }

    /// <summary>光标底下那一行。鼠标被弹层捕获着，e.OriginalSource 永远是弹层自己，只能自己命中测试。</summary>
    private static ComboBoxItem? RowUnderPoint(FrameworkElement host, Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > host.ActualWidth || point.Y > host.ActualHeight)
        {
            return null;
        }

        return VisualTreeHelper.HitTest(host, point)?.VisualHit is DependencyObject hit
            ? FindAncestor<ComboBoxItem>(hit)
            : null;
    }

    private static ComboBoxItem? RowFrom(object? source) =>
        source is DependencyObject node ? FindAncestor<ComboBoxItem>(node) : null;

    /// <summary>跟手纸片：写上当条的名字，贴在光标右下角。第一次拖动时才建（弹层是独立 HWND，只有 Popup 浮得上去）。</summary>
    private void OpenModelGhost()
    {
        if (_modelDragName is null)
        {
            return;
        }

        if (_modelGhost is null)
        {
            _modelGhostText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
            _modelGhostText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");

            var accent = new Border
            {
                Width = 3,
                Height = 13,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            accent.SetResourceReference(Border.BackgroundProperty, "Brush.Primary");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(accent);
            row.Children.Add(_modelGhostText);

            var card = new Border
            {
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 10, 5),
                Child = row,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
            card.SetResourceReference(Border.CornerRadiusProperty, "Radius.Chip");
            card.SetResourceReference(Border.EffectProperty, "Shadow.Popup");

            _modelGhost = new Popup
            {
                // 实测：Relative / RelativePoint 是把弹层的右上角对到偏移点上（整体左移一个自身宽度），
                // 只有 Absolute 才是「左上角落在偏移点」，所以位置自己算（见 MoveModelGhost）。
                Placement = PlacementMode.Absolute,
                PlacementTarget = this,
                AllowsTransparency = true,
                Focusable = false,
                IsHitTestVisible = false,
                Child = card,
            };
        }

        _modelGhostText!.Text = _modelDragName;
        _modelGhost.IsOpen = true;
        MoveModelGhost();
    }

    private void MoveModelGhost()
    {
        if (_modelGhost is null)
        {
            return;
        }

        // Absolute 的偏移是屏幕坐标（DIP），PointToScreen 给的是物理像素：高 DPI 屏上先除回 DIP
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = PointToScreen(Mouse.GetPosition(this));
        _modelGhost.HorizontalOffset = (screen.X / dpi.DpiScaleX) + 16;
        _modelGhost.VerticalOffset = (screen.Y / dpi.DpiScaleY) + 12;
    }

    private void CloseModelGhost()
    {
        if (_modelGhost is { IsOpen: true })
        {
            _modelGhost.IsOpen = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>按导航页签名切换设置分区；Doctor 的“打开设置”操作复用这里。</summary>
    public void NavigateToSection(string section)
    {
        var header = section switch
        {
            "hotkeys" => "热键",
            "advanced" => "高级",
            "updates" => "更新",
            "diagnostics" => "诊断",
            _ => section,
        };

        var tab = NavTabs.Items.OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header as string, header, StringComparison.Ordinal));
        if (tab is null)
        {
            return;
        }

        // Doctor / 托盘深链可能指向默认收起的高级分区；先展开导航再选中，避免“动作执行了但页面不见”。
        if (header is "更新" or "诊断" or "定制印" or "高级")
        {
            _viewModel.ShowAdvancedSettings = true;
        }

        NavTabs.SelectedItem = tab;
        Activate();
    }
}





