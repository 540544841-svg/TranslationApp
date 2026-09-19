using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Serilog;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using TranslationApp.Core.Settings;
using TranslationApp.Interop;

namespace TranslationApp.Windows;

/// <summary>
/// 钉图窗口：把框选到的图片「钉」在屏幕上——贴住原选区位置，可拖动、可滚轮缩放、可切原文/译文、可关闭。
/// 形态：无边框 + 置顶 + 不占任务栏 + 不进 Alt+Tab（WS_EX_TOOLWINDOW）+ 初始不激活（SWP_NOACTIVATE）；
/// <b>本窗口自身永不自动隐藏</b>——失焦隐藏是小窗（QuickWindow）的机制，钉图是用户主动放上去的常驻物。
///
/// 定位与尺寸（与 FR-025 同一范式，绝不回退到 WPF 的 Left/Top DIP 定位）：
/// 单一不变式 <c>窗口物理尺寸 = 图像像素 × 缩放倍数</c>，位置由 <c>SetWindowPos</c> 以物理像素落地并
/// <c>GetWindowRect</c> 校验；DIP 尺寸只是它除以所在屏缩放的投影。
///
/// 原文/译文切换（14.3.6）与工具条/右键菜单（14.3.6 / 14.3.9）的规则全部来自 Core 的
/// <see cref="PinOverlayRules"/> / <see cref="PinToolbarRules"/> / <see cref="PinShortcuts"/>（可单测），
/// 本类只做「把状态映射到 WPF 元素与动画」这一件事：
/// 切换 = 覆盖层整体 <c>Opacity</c> 0↔1 的 120 ms 交叉淡入（不准备两张图、不改窗口尺寸 → 无布局抖动）；
/// 切换状态与透明度都只存在本窗口的字段里，**只影响这一张钉图**、不写回设置。
/// <para>v1.2 修复批（④/②）：根部为阴影外框卡片（Margin=16 + Radius.Card + Shadow.Window），
/// 工具条移入图片下方的**常驻条带**（不再浮在图片上、不再自动淡出），
/// 不变式改写为「窗口物理尺寸 = 图像像素 × zoom + 2×frame + chrome（条带 + 面板）」。</para>
/// </summary>
public partial class PinWindow : Window
{
    /// <summary>缩放反馈条的停留时长（够看清又不长期遮挡内容）。</summary>
    private const int ZoomChipHoldMs = 1200;

    /// <summary>新钉图初始不透明度的允许范围（14.6 的 PinOpacity 语义）。</summary>
    private const double MinOpacity = 0.3;

    /// <summary>四周阴影边距（DIP；④）：与 XAML 中 <c>ChromeBorder.Margin</c> 同源（由构造函数赋值），
    /// 参与「窗口物理尺寸 = 图像像素 × zoom + 2×frame + chrome」的不变式与缩放上限计算。</summary>
    private const double PinShadowMarginDip = 16;

    /// <summary>工具条条带的兜底高度（DIP，④）：按钮行 32 + 上下留白；布局完成后改用实测 ActualHeight。</summary>
    private const double BandFallbackDip = 44;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _chipTimer;
    private readonly ScaleTransform _toolbarScale = new(1, 1);
    private readonly int _imageWidth;
    private readonly int _imageHeight;

    /// <summary>
    /// 右键菜单项的基础文案（动作 → 文案）：禁用项需要在文案后追加原因
    /// （菜单项一旦禁用就收不到鼠标事件，只挂 ToolTip 用户看不到），刷新时先取回基础文案再拼。
    /// </summary>
    private readonly Dictionary<PinToolbarAction, string> _menuHeaders = [];

    /// <summary>翻译失败后的重试入口（由截图流程注入；null = 本张钉图没有可重试的翻译）。</summary>
    private readonly Func<Task<PinContent>>? _retryAsync;

    /// <summary>「在小窗中打开」入口（14.3.8）。</summary>
    private readonly Action? _openInQuickWindow;

    /// <summary>强制翻译入口（v1.2 ③-C：「识别语言与目标语言相同」跳过态的补救；null = 不可用）。</summary>
    private readonly Func<Task<PinContent>>? _forceTranslateAsync;

    /// <summary>覆盖块圆角（14.3.9：`Radius.Control`(8 DIP) 按 k 换算到图像像素，**相机换算而非写死**）。</summary>
    private readonly double _coverRadiusDip;

    /// <summary>阴影外框卡片的圆角（`Radius.Card`，④：图片区据此做圆角裁剪，与外框一体）。</summary>
    private readonly double _cardRadiusDip;

    private BitmapSource? _source;
    private PinZoomLimits _limits;

    /// <summary>本窗口的内容（批 4c 传入真实 OCR 段落与译文；批 4b 为占位内容）。</summary>
    private PinContent? _content;

    /// <summary>当前显示原文还是译文——**每张钉图各自持有**，互不影响、不写回设置。</summary>
    private PinTextLayer _layer = PinTextLayer.Original;

    /// <summary>用户是否手动切换过（切换过则后到的译文不覆盖用户的选择）。</summary>
    private bool _userToggledLayer;

    /// <summary>条带当前是否放得下（窄窗口缩到下限以下时条带整体隐藏，全部动作仍有快捷键与右键菜单）。</summary>
    private bool _toolbarPlaceable = true;

    /// <summary>本窗口所在显示器的工作区（物理像素）；随窗口跨屏而刷新。</summary>
    private PhysicalRect _workArea;

    private double _zoom = 1.0;
    private double _dpiScale = 1.0;

    /// <summary>
    /// 本窗口的物理矩形——定位与缩放锚点的唯一真源。
    /// 只由本类写入（摆放/拖动后以 GetWindowRect 回读），绝不回读 WPF 的 Left/Top/ActualWidth 参与计算。
    /// </summary>
    private PhysicalRect _rect;

    /// <summary>物理摆放进行中：期间 OnDpiChanged 让路，避免冲掉本次摆放（FR-025 的同一手法）。</summary>
    private bool _positioning;

    /// <summary>下方译文面板占用的物理高度（模式 C）。</summary>
    private int _panelPhysHeight;

    /// <summary>四周阴影边距的单侧物理长度（④：= round(PinShadowMarginDip × 屏缩放)，不随 zoom 变化）。</summary>
    private int _framePhys;

    /// <summary>界面 chrome 占用的物理高度（工具条条带 + 译文面板；不随 zoom 变化，跨屏/内容变化时重算）。</summary>
    private int _chromePhysHeight;

    /// <summary>重试进行中（防重复点击）。</summary>
    private bool _retrying;

    /// <summary>强制翻译进行中（防重复点击，③-C）。</summary>
    private bool _forceTranslating;

    /// <summary>已安排过一次 Loaded 优先级重摆放（防重入）。</summary>
    private bool _dpiHopPending;

    /// <summary>
    /// 双击 = 原文/译文切换（14.3.6，明确**不是**关闭）。切换已由本窗口完成，此事件仅作外部观察/扩展点
    /// （订阅方不要重复切换）。
    /// </summary>
    public event EventHandler? SwitchTextLayerRequested;

    /// <summary>
    /// <paramref name="imageWidth"/>/<paramref name="imageHeight"/> 是位图的图像像素尺寸（可能已按单张上限降采样），
    /// <paramref name="originX"/>/<paramref name="originY"/> 是原选区左上角的屏幕物理坐标（钉图就贴在它的位置）。
    /// <paramref name="content"/> 为可选的译文与 OCR 布局——批 4c 传入真实数据即可工作，
    /// 不传时用占位内容（批 4b：让切换与过渡在真机上看得出效果）。
    /// </summary>
    public PinWindow(
        BitmapSource source, int imageWidth, int imageHeight, int originX, int originY,
        AppSettings settings, PinContent? content = null,
        Func<Task<PinContent>>? retryAsync = null, Action? openInQuickWindow = null,
        Func<Task<PinContent>>? forceTranslateAsync = null)
    {
        InitializeComponent();

        _source = source;
        _imageWidth = Math.Max(1, imageWidth);
        _imageHeight = Math.Max(1, imageHeight);
        _settings = settings;
        _content = content ?? PinContent.Placeholder(_imageWidth, _imageHeight);
        _retryAsync = retryAsync;
        _openInQuickWindow = openInQuickWindow;
        _forceTranslateAsync = forceTranslateAsync;
        _coverRadiusDip = ((CornerRadius)FindResource("Radius.Control")).TopLeft;
        _cardRadiusDip = ((CornerRadius)FindResource("Radius.Card")).TopLeft;

        ContentImage.Source = source;
        Opacity = Math.Clamp(settings.PinOpacity, MinOpacity, 1.0);
        ChromeBorder.Margin = new Thickness(PinShadowMarginDip); // 阴影边距与几何计算的 frame 同源（④）

        _chipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ZoomChipHoldMs) };
        _chipTimer.Tick += (_, _) =>
        {
            _chipTimer.Stop();
            ZoomChip.Visibility = Visibility.Collapsed;
        };

        // 工具条带常驻（④）：不再自动淡出，只保留「窄窗口按比例缩小 / 放不下整体隐藏」
        ToolbarStrip.LayoutTransform = _toolbarScale;

        // 双击 = 原文/译文切换（14.3.6；不是关闭）。挂在窗口上而非 XAML 里：
        // PreviewMouseDoubleClick 是 Control 的 CLR 事件，XAML 编译器不接受它作为事件特性。
        PreviewMouseDoubleClick += OnImageDoubleClick;
        // 激活指示（事件在代码里接线，避免 XAML 事件解析差异）
        Activated += (_, _) => UpdateActiveFrame(true);
        Deactivated += (_, _) => UpdateActiveFrame(false);
        SizeChanged += (_, _) => UpdateToolbarPlacement();
        ToolbarStrip.SizeChanged += (_, _) => UpdateToolbarPlacement();
        ToolbarHost.SizeChanged += (_, _) => ApplyChrome(); // 条带增高（如 StatusChip 出现）→ chrome 变 → 重摆
        ContentArea.SizeChanged += (_, _) => ApplyContentClip(); // 图片区按卡片圆角裁剪（④）
        Loaded += (_, _) =>
        {
            UpdateToolbarPlacement();
            ApplyContentClip();
            LogOverlayState("已显示"); // 布局完成后的真实画布尺寸：可据此判断覆盖层是否被裁掉或画在界外
        };

        // 初始按 1.0 倍（逐像素还原原屏画面），再按工作区约束夹取：
        // 选区比工作区还大时允许 < 1，保证钉图一出现就完整可见（AC 14）
        RefreshEnvironment(new PhysicalRect(originX, originY, _imageWidth, _imageHeight));
        ApplyPanel(); // 面板高度必须先定下来：它参与窗口物理尺寸与缩放上限（模式 C）
        _chromePhysHeight = -1; // 触发 ApplyChrome 首算（条带布局未完成前用兜底高度）
        ApplyChrome();
        _limits = ResolveLimits();
        _zoom = PinLayout.ClampZoom(1.0, _limits);
        _rect = PinLayout.InitialRectWithChrome(
            originX, originY, _imageWidth, _imageHeight, _workArea, _zoom, _framePhys, _chromePhysHeight);
        RefreshEnvironment(_rect);

        // 初始层：有译文且设置要求优先显示译文时看译文，否则看原文（14.3.8）。
        // 本批为占位内容（无译文）→ 默认显示原文，用户按空格/双击即看到覆盖层的淡入。
        _layer = PinOverlayRules.Default(_content.HasTranslation, _settings.OcrInPlaceReplace);
        RebuildOverlay();
        ApplyLayer(_layer, animate: false);

        Log.Debug(
            "钉图创建：图像 {ImageW}×{ImageH} px，zoom {Zoom:0.###}，物理 {W}×{H} px（阴影边距 {Frame} px，chrome {Chrome} px），缩放 {Scale}，范围 [{Min:0.###}, {Max:0.###}]，覆盖块 {Blocks} 个，初始层 {Layer}",
            _imageWidth, _imageHeight, _zoom, _rect.Width, _rect.Height, _framePhys, _chromePhysHeight, _dpiScale,
            _limits.Min, _limits.Max, _content.Blocks.Count, _layer);
    }

    /// <summary>本窗口占用的像素数（管理器据此维护总量上限）。</summary>
    public long Pixels => (long)_imageWidth * _imageHeight;

    /// <summary>当前缩放倍数（真机复验用）。</summary>
    public double Zoom => _zoom;

    /// <summary>当前显示层（真机复验用）。</summary>
    public PinTextLayer CurrentLayer => _layer;

    /// <summary>本窗口的物理矩形（真机复验用，与 GetWindowRect 一致）。</summary>
    public PhysicalRect PhysicalBounds => _rect;

    /// <summary>
    /// 替换内容（译文/段落到位后调用，界面结构不变，只换填充内容；模式切换时同步面板与窗口高度）。
    /// <paramref name="keepUserChoice"/> 为 true 时不推翻用户已手动选择的显示层。
    /// </summary>
    public void UpdateContent(PinContent content, bool keepUserChoice = true)
    {
        _content = content;
        ApplyPanel();
        RebuildOverlay();
        UpdateToolbarState(); // StatusChip / 按钮可见性会改变条带高度，下面的 ApplyChrome 据此重算并重摆
        ApplyChrome();

        if (!_userToggledLayer || !keepUserChoice)
        {
            ApplyLayer(PinOverlayRules.Default(content.HasTranslation, _settings.OcrInPlaceReplace), animate: true);
        }
        else
        {
            UpdateToolbarState();
        }

        Log.Debug("钉图内容已更新：覆盖块 {Blocks} 个，面板={HasPanel}，有译文={HasTranslation}，可重试={CanRetry}，当前层 {Layer}",
            content.Blocks.Count, content.HasPanel, content.HasTranslation, content.CanRetry, _layer);
    }

    /// <summary>
    /// 应用下方译文面板（模式 C）：面板高度计入窗口物理高度（14.3.7），
    /// 面板文本与图片内容无关，因此走**跟主题的**界面令牌（14.3.9）。
    /// </summary>
    private void ApplyPanel()
    {
        var content = _content;
        var hasPanel = content?.HasPanel == true && content.PanelHeightDip > 0;
        if (hasPanel)
        {
            PanelTextBlock.Text = content!.PanelText;
            PanelHost.Height = content.PanelHeightDip;
            PanelHost.Visibility = Visibility.Visible;
        }
        else
        {
            PanelTextBlock.Text = string.Empty;
            PanelHost.Height = 0;
            PanelHost.Visibility = Visibility.Collapsed;
        }

        _panelPhysHeight = hasPanel
            ? Math.Max(1, (int)Math.Round(content!.PanelHeightDip * _dpiScale))
            : 0;
    }

    /// <summary>工具条条带占用的 DIP 高度（④）：布局完成后用实测值；条带隐藏时为 0；布局前用兜底常量。</summary>
    private double BandDip =>
        ToolbarHost.Visibility == Visibility.Collapsed
            ? 0
            : ToolbarHost.ActualHeight > 0 ? ToolbarHost.ActualHeight : BandFallbackDip;

    /// <summary>
    /// 重算界面 chrome（条带 + 面板）与阴影边距的物理量并按需重摆（v1.2 ④）：
    /// chrome 不随 zoom 变化，但条带高度（StatusChip 显隐）、面板显隐与屏缩放都会改变它；
    /// 变化后必须重新成立「窗口物理高度 = 图像像素 × zoom + 2×frame + chrome」（14.3.7 的尺寸同步）。
    /// </summary>
    private void ApplyChrome()
    {
        _framePhys = Math.Max(0, (int)Math.Round(PinShadowMarginDip * _dpiScale));
        var chromePhys = Math.Max(0, (int)Math.Round(BandDip * _dpiScale)) + _panelPhysHeight;
        if (chromePhys == _chromePhysHeight)
        {
            return;
        }

        _chromePhysHeight = chromePhys;
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            return; // 尚未显示：ShowPinned 会按最新的 _rect 摆放
        }

        _limits = ResolveLimits();
        _zoom = PinLayout.ClampZoom(_zoom, _limits);
        PlaceAndVerify(PinLayout.InitialRectWithChrome(
            _rect.Left, _rect.Top, _imageWidth, _imageHeight, _workArea, _zoom, _framePhys, _chromePhysHeight));
        UpdateScalingMode();
        RebuildOverlay();
    }

    /// <summary>图片区按卡片圆角裁剪（④）：图片与覆盖层收进 Radius.Card 圆角内，与阴影外框观感一体。</summary>
    private void ApplyContentClip()
    {
        var width = ContentArea.ActualWidth;
        var height = ContentArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Min(_cardRadiusDip, Math.Min(width, height) / 2);
        ContentArea.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    /// <summary>
    /// 摆放并显示（不激活）：Show 前先按目标物理尺寸设好 DIP 尺寸（避免在旧位置/旧尺寸闪现），
    /// Show 后再物理摆放一次覆盖 WPF 自身的几何，最后安排一次 Loaded 优先级重摆放兜底（跨屏 DPI 跳变）。
    /// </summary>
    public void ShowPinned()
    {
        ApplyDipSize(_rect);
        Show();
        PlaceAndVerify(_rect);
        ScheduleDeferredReposition();
        UpdateToolbarPlacement();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 置顶 + 工具窗口（不进 Alt+Tab）。不用 WS_EX_NOACTIVATE：单击图片即激活是 14.3.6 的预期行为。
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd, NativeMethods.GWL_EXSTYLE,
            new IntPtr(exStyle | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// 跨屏会触发本回调，WPF 会按 DIP 保持尺寸（物理尺寸被改写，破坏「物理尺寸 = 图像像素 × 缩放」）。
    /// 摆放期间让路，其余情况安排一次 Loaded 优先级的重摆放（FR-025 步骤 9 的同一处理）。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Log.Debug("钉图 DPI 变化：{Old} → {New}（物理摆放中={Positioning}）",
            oldDpi.PixelsPerDip, newDpi.PixelsPerDip, _positioning);

        if (_positioning)
        {
            return;
        }

        ScheduleDeferredReposition();
    }

    private void ScheduleDeferredReposition()
    {
        if (_dpiHopPending)
        {
            return;
        }

        _dpiHopPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _dpiHopPending = false;
            Reposition();
        }));
    }

    // ---------------- 拖动移动（在图片上按住左键拖动 = 移动窗口，一次操作同时激活） ----------------

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        BringToTop();
        try
        {
            // DragMove 由系统在物理像素上移动窗口，并用 HTCAPTION 顺带激活本窗口（14.3.6：拖动即激活）
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已释放（快速点击），忽略
        }

        SyncRectFromWindow(); // 拖动后位置以 GetWindowRect 为准；不写回任何设置（钉图是临时物）
    }

    /// <summary>点击某张钉图时把它置到最上层（14.3.6 层级）；只改层级，不动位置与尺寸。</summary>
    private void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ScreenInterop.SetWindowPos(
                hwnd, ScreenInterop.HWND_TOPMOST, 0, 0, 0, 0,
                ScreenInterop.SWP_NOMOVE | ScreenInterop.SWP_NOSIZE | ScreenInterop.SWP_NOACTIVATE);
        }
    }

    private void SyncRectFromWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && ScreenInterop.GetWindowRect(hwnd, out var actual))
        {
            _rect = new PhysicalRect(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
            Log.Debug("钉图拖动完成：({Left},{Top}) px，{Width}×{Height} px",
                _rect.Left, _rect.Top, _rect.Width, _rect.Height);
        }
    }

    // ---------------- 滚轮缩放（等比步进 + 鼠标锚点 + 工作区上限 + 边界反馈） ----------------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 分派优先级（14.3.6，互斥、不叠加）：Ctrl（透明度）> Shift（精细缩放）> 普通缩放。
        // Ctrl 优先是必须明确的一条：它与缩放共用滚轮，若允许叠加则同时按住时两者都会变。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            AdjustOpacity(PinLayout.StepOpacity(Opacity, e.Delta));
            e.Handled = true;
            return;
        }

        var local = e.GetPosition(this);
        // 锚点分数相对**图片区矩形**（扣除四周阴影边距与底部条带/面板，④）：与 ZoomAtWithChrome 的
        // 锚点定义一致，否则缩放会漂移。取 DIP 比值作分数：与 DPI 无关，无需再算鼠标的屏幕物理坐标。
        var frameDip = PinLayout.DipLength(_framePhys, _dpiScale);
        var chromeDip = PinLayout.DipLength(_chromePhysHeight, _dpiScale);
        var imageAreaWidth = Math.Max(1, ActualWidth - (2 * frameDip));
        var imageAreaHeight = Math.Max(1, ActualHeight - (2 * frameDip) - chromeDip);
        var fractionX = (local.X - frameDip) / imageAreaWidth;
        var fractionY = (local.Y - frameDip) / imageAreaHeight;

        var fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var target = PinLayout.StepZoom(_zoom, e.Delta, _settings.PinZoomStep, fine, _limits);
        ApplyZoom(target, fractionX, fractionY);
        e.Handled = true;
    }

    private void ApplyZoom(double target, double fractionX, double fractionY)
    {
        var zoom = PinLayout.ClampZoom(target, _limits);
        if (Math.Abs(zoom - _zoom) < 0.0001)
        {
            // 已在上下限：给出可感知反馈，不静默无响应（14.3.6 缩放范围）
            var atMax = zoom >= _limits.Max - 0.0001;
            ShowToast(atMax ? $"已放到最大 {zoom * 100:0}%" : $"已缩到最小 {zoom * 100:0}%");
            return;
        }

        _zoom = zoom;
        PlaceAndVerify(PinLayout.ZoomAtWithChrome(
            _rect, fractionX, fractionY, _zoom, _imageWidth, _imageHeight, _framePhys, _chromePhysHeight, _workArea));
        UpdateScalingMode();
        RebuildOverlay(); // 缩放后 k 变化：覆盖块与字号按同一系数重算，保证与图片严格对齐（AC 5）
        UpdateToolbarState();
        ShowToast(PinToolbarRules.FormatZoom(_zoom));
        Log.Debug("钉图缩放：zoom={Zoom:0.###}，物理 {Width}×{Height} px，锚点 ({Fx:0.###},{Fy:0.###})",
            _zoom, _rect.Width, _rect.Height, fractionX, fractionY);
    }

    private void ShowToast(string text)
    {
        ZoomText.Text = text;
        ZoomChip.Visibility = Visibility.Visible;
        _chipTimer.Stop();
        _chipTimer.Start();
    }

    // ---------------- 透明度（14.3.6：Ctrl+滚轮 / 右键菜单「恢复不透明」） ----------------

    /// <summary>
    /// 单张钉图的透明度（14.3.6：范围 <c>[0.3, 1.0]</c>）：**只作用于这一张、绝不写回设置**
    /// （新钉图的初始值取 <c>PinOpacity</c>，只在构造时读一次）。到达上下限给出可感知反馈，不静默无响应。
    /// </summary>
    private void AdjustOpacity(double target)
    {
        var opacity = PinLayout.ClampOpacity(target);
        if (Math.Abs(opacity - Opacity) < 0.0001)
        {
            ShowToast(opacity >= PinLayout.MaxOpacity - 0.0001
                ? "已是不透明"
                : $"已达最低透明度 {PinLayout.MinOpacity * 100:0}%");
            return;
        }

        Opacity = opacity;
        ShowToast($"不透明度 {opacity * 100:0}%");
        Log.Debug("钉图透明度已调整：{Opacity:0.00}（仅本次窗口，不写回设置）", opacity);
    }

    /// <summary>「恢复不透明」（右键菜单）：已是不透明时由 <see cref="PinToolbarRules"/> 禁用并说明原因。</summary>
    private void ResetOpacity()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ResetOpacity, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "当前已是不透明");
            return;
        }

        AdjustOpacity(PinLayout.MaxOpacity);
    }

    // ---------------- 右键菜单（14.3.6：键盘与工具条之外的兜底入口） ----------------

    /// <summary>
    /// 菜单每次打开时刷新：启用/禁用与**禁用原因**一律由 <see cref="PinToolbarRules"/> 决定
    /// （与工具条同一份逻辑，两处不可能走偏）。菜单项禁用后收不到鼠标事件，故原因直接追加到文案上。
    /// </summary>
    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var availability = CurrentAvailability;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not PinToolbarAction action)
            {
                continue;
            }

            if (!_menuHeaders.TryGetValue(action, out var header))
            {
                header = item.Header as string ?? action.ToString();
                _menuHeaders[action] = header;
            }

            var state = PinToolbarRules.Resolve(action, availability);
            item.IsEnabled = state.Enabled;
            item.Header = state.Enabled || string.IsNullOrWhiteSpace(state.DisabledReason)
                ? header
                : $"{header}（{state.DisabledReason}）";

            // 与工具条同一策略：没有可重试 / 可强制翻译的场景时不出现（禁用着占位只会让人以为功能坏了）
            if (action == PinToolbarAction.RetryTranslation)
            {
                item.Visibility = availability.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (action == PinToolbarAction.ForceTranslate)
            {
                item.Visibility = availability.CanForceTranslate ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>菜单项 → 动作：与键盘 / 工具条共用 <see cref="Execute"/>，禁用项再兜一层原因提示。</summary>
    private void OnMenuActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: PinToolbarAction action })
        {
            return;
        }

        var state = PinToolbarRules.Resolve(action, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "当前不可用");
            return;
        }

        _ = Execute(action);
    }

    // ---------------- 原文 / 译文切换（14.3.6） ----------------

    private PinAvailability CurrentAvailability => new(
        _content?.HasOverlay == true,
        _content?.HasTranslation == true,
        PinOverlayRules.IsOverlayVisible(_layer),
        Math.Abs(_zoom - 1.0) < 0.0001,
        _retryAsync is not null && _content?.CanRetry == true,
        _content?.HasSource == true,
        PinLayout.IsFullyOpaque(Opacity),
        _forceTranslateAsync is not null && _content?.CanForceTranslate == true);

    /// <summary>
    /// 应用显示层：覆盖层整体 <c>Opacity</c> 在 0↔1 之间 120 ms 交叉淡入（`Duration.Fast`），
    /// 窗口尺寸不变 → 无布局抖动；淡出到 0 后折叠覆盖层（不再渲染，也便于批 4c 的大段落）。
    /// </summary>
    private void ApplyLayer(PinTextLayer layer, bool animate)
    {
        _layer = layer;
        var target = PinOverlayRules.TargetOpacity(layer);
        if (target > 0)
        {
            OverlayLayer.Visibility = Visibility.Visible;
        }

        if (!animate)
        {
            OverlayLayer.BeginAnimation(OpacityProperty, null);
            OverlayLayer.Opacity = target;
            if (target <= 0)
            {
                OverlayLayer.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            var duration = (Duration)FindResource("Duration.Fast");
            var animation = new DoubleAnimation(target, duration);
            animation.Completed += (_, _) =>
            {
                if (_layer == PinTextLayer.Original)
                {
                    OverlayLayer.Visibility = Visibility.Collapsed;
                }
            };
            OverlayLayer.BeginAnimation(OpacityProperty, animation);
        }

        UpdateToolbarState();
    }

    /// <summary>切换原文/译文（按钮 / 快捷键 / 双击共用；<paramref name="manual"/> 标记用户主动切换）。</summary>
    private void ToggleTextLayer(bool manual)
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ToggleTextLayer, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "没有可切换的内容");
            return;
        }

        if (manual)
        {
            _userToggledLayer = true;
        }

        var next = PinOverlayRules.Toggle(_layer);
        ApplyLayer(next, animate: true);
        Log.Debug("钉图原文/译文切换：{Layer}（手动={Manual}）", next, manual);
    }

    private void OnToggleLayerClick(object sender, RoutedEventArgs e) => ToggleTextLayer(manual: true);

    /// <summary>复制译文：无译文时按钮禁用且键盘触发也给出原因，绝不静默无效。</summary>
    private void CopyTranslation()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.CopyTranslation, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "尚无译文");
            return;
        }

        var text = _content?.TranslatedText ?? string.Empty;
        try
        {
            Clipboard.SetText(text);
            ShowToast("已复制译文");
            Log.Debug("钉图复制译文：{Length} 字", text.Length); // 只记长度，不记内容（14.3.10）
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图复制译文失败（剪贴板被占用）");
            ShowToast("复制失败，请重试");
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyTranslation();

    /// <summary>
    /// 「在小窗中打开」（14.3.8）：把识别文本 + 译文带回主流程（可编辑、可再翻译、可入库）。
    /// 无原文时给出原因；回调缺失（占位钉图）时同样不静默无效。按钮与 `Ctrl+Enter` 共用。
    /// </summary>
    private void OpenInQuickWindow()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.OpenInQuickWindow, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "没有可打开的原文");
            return;
        }

        try
        {
            _openInQuickWindow?.Invoke();
            Log.Debug("钉图：在小窗中打开（原文 {Length} 字）", _content?.SourceText?.Length ?? 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图「在小窗中打开」失败");
            ShowToast("打开小窗失败，请重试");
        }
    }

    private void OnOpenQuickClick(object sender, RoutedEventArgs e) => OpenInQuickWindow();

    /// <summary>重试：重新翻译并刷新本张钉图（不重建窗口、位置与缩放不变）。按钮 / 菜单 / `Ctrl+R` 共用。</summary>
    private async Task RetryTranslationAsync()
    {
        // 整段都包在 try 里：本方法由 async void 的事件处理器调用，前置段（状态判定、提示条）
        // 抛出的异常会直接逃出 async void 并崩掉进程（14.3.2 的可靠性约束：绝不因一次重试失败而中断）
        try
        {
            var state = PinToolbarRules.Resolve(PinToolbarAction.RetryTranslation, CurrentAvailability);
            if (!state.Enabled || _retrying || _retryAsync is null)
            {
                ShowToast(state.DisabledReason ?? "当前不需要重试");
                return;
            }

            _retrying = true;
            UpdateToolbarState();
            ShowToast("正在重试翻译…");

            var content = await _retryAsync();
            UpdateContent(content, keepUserChoice: false);
            ShowToast(content.HasTranslation ? "翻译完成" : content.StatusMessage ?? "重试仍未成功");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图重试翻译失败");
            ShowToast("重试失败，请稍后再试");
        }
        finally
        {
            _retrying = false;
            UpdateToolbarState();
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RetryTranslationAsync();

    /// <summary>
    /// 强制翻译（v1.2 ③-C）：「识别语言与目标语言相同」被跳过时，把源语言置回自动检测重新翻译并刷新本张钉图。
    /// 按钮 / 菜单共用；与重试同一套防重入与异常兜底（async void 事件链不得带崩进程）。
    /// </summary>
    private async Task ForceTranslateAsync()
    {
        try
        {
            var state = PinToolbarRules.Resolve(PinToolbarAction.ForceTranslate, CurrentAvailability);
            if (!state.Enabled || _forceTranslating || _forceTranslateAsync is null)
            {
                ShowToast(state.DisabledReason ?? "当前不需要强制翻译");
                return;
            }

            _forceTranslating = true;
            UpdateToolbarState();
            ShowToast("正在翻译…");

            var content = await _forceTranslateAsync();
            UpdateContent(content, keepUserChoice: false);
            ShowToast(content.HasTranslation || content.HasPanel
                ? "翻译完成"
                : content.StatusMessage ?? "翻译未成功");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图强制翻译失败");
            ShowToast("翻译失败，请稍后再试");
        }
        finally
        {
            _forceTranslating = false;
            UpdateToolbarState();
        }
    }

    private async void OnForceTranslateClick(object sender, RoutedEventArgs e) => await ForceTranslateAsync();

    // ---------------- 工具条（出现 / 淡出 / 启用禁用 / 摆放） ----------------

    private void UpdateToolbarState()
    {
        var availability = CurrentAvailability;
        ApplyButtonState(ToggleLayerButton, PinToolbarAction.ToggleTextLayer, availability, "切换原文/译文（空格 / T / 双击）");
        ApplyButtonState(CopyButton, PinToolbarAction.CopyTranslation, availability, "复制译文（Ctrl+C）");
        ApplyButtonState(OpenQuickButton, PinToolbarAction.OpenInQuickWindow, availability, "在小窗中打开（可编辑、可入库）");
        ApplyButtonState(RetryButton, PinToolbarAction.RetryTranslation, availability, "重新翻译（网络恢复后可用）");
        ApplyButtonState(ForceTranslateButton, PinToolbarAction.ForceTranslate, availability, "把源语言置回自动检测并强制翻译");
        ApplyButtonState(ZoomResetButton, PinToolbarAction.ResetZoom, availability, "重置到 100%（Ctrl+0）");
        ApplyButtonState(CloseButton, PinToolbarAction.Close, availability, "关闭钉图（Esc）");

        RetryButton.Visibility = _retryAsync is not null && _content?.CanRetry == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ForceTranslateButton.Visibility = _forceTranslateAsync is not null && _content?.CanForceTranslate == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenQuickButton.Visibility = _openInQuickWindow is not null ? Visibility.Visible : Visibility.Collapsed;

        ZoomResetText.Text = PinToolbarRules.FormatZoom(_zoom);

        var status = _content?.StatusMessage;
        StatusText.Text = status ?? string.Empty;
        StatusChip.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;

        UpdateToolbarPlacement();
    }

    /// <summary>
    /// 按钮状态一律由 <see cref="PinToolbarRules"/> 决定：禁用时把**原因**放进 ToolTip
    /// （`ToolTipService.ShowOnDisabled` 已开，禁用也能看到为什么），激活态用 Tag 驱动样式。
    /// </summary>
    private static void ApplyButtonState(
        Button button, PinToolbarAction action, PinAvailability availability, string hint)
    {
        var state = PinToolbarRules.Resolve(action, availability);
        button.IsEnabled = state.Enabled;
        button.Tag = PinToolbarRules.IsToggleOn(action, availability) ? "on" : null;
        button.ToolTip = state.Enabled ? hint : state.DisabledReason;
    }

    /// <summary>
    /// 条带摆放（v1.2 ④）：工具条在图片下方的常驻条带里（水平居中、永不遮挡图片文字），
    /// 只按窗口宽度判断放不放得下（<see cref="PinToolbarRules.ResolveBandPlacement"/>）；
    /// 放不下时整体隐藏（条带行高随之归零，<see cref="ApplyChrome"/> 会收缩窗口高度）。
    /// 布局尚未完成时直接返回，等 SizeChanged / Loaded 再算。
    /// </summary>
    private void UpdateToolbarPlacement()
    {
        var windowWidth = ActualWidth;
        if (windowWidth <= 0 || ToolbarStrip.ActualWidth <= 0 || ToolbarStrip.ActualHeight <= 0)
        {
            return;
        }

        var placement = PinToolbarRules.ResolveBandPlacement(
            windowWidth, ToolbarStrip.ActualWidth, PinToolbarRules.MarginDip);
        if (_toolbarPlaceable == placement.Visible && _toolbarScale.ScaleX == placement.Scale)
        {
            return; // 无变化：避免重复触发布局循环
        }

        _toolbarPlaceable = placement.Visible;
        _toolbarScale.ScaleX = placement.Scale;
        _toolbarScale.ScaleY = placement.Scale;
        ToolbarHost.Visibility = placement.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 关闭与键盘 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var action = PinShortcuts.Resolve(
            MapKey(e.Key),
            (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0);
        if (action is null)
        {
            return;
        }

        e.Handled = Execute(action.Value);
    }

    /// <summary>执行动作；被禁用的动作给出原因提示（不静默无效），返回是否已处理该按键。</summary>
    private bool Execute(PinToolbarAction action)
    {
        switch (action)
        {
            case PinToolbarAction.Close:
                ClosePin(); // Esc 关闭当前钉图（14.3.6）
                return true;
            case PinToolbarAction.ToggleTextLayer:
                ToggleTextLayer(manual: true);
                return true;
            case PinToolbarAction.CopyTranslation:
                CopyTranslation();
                return true;
            case PinToolbarAction.ResetZoom:
                ResetZoom();
                return true;
            case PinToolbarAction.ZoomIn:
                StepZoom(1);
                return true;
            case PinToolbarAction.ZoomOut:
                StepZoom(-1);
                return true;
            case PinToolbarAction.RetryTranslation:
                _ = RetryTranslationAsync();
                return true;
            case PinToolbarAction.ForceTranslate:
                _ = ForceTranslateAsync();
                return true;
            case PinToolbarAction.ResetOpacity:
                ResetOpacity();
                return true;
            case PinToolbarAction.OpenInQuickWindow:
                OpenInQuickWindow();
                return true;
            default:
                return false;
        }
    }

    /// <summary>WPF 键位 → Core 的键名（缩放/切换/复制/关闭/重试/开小窗/恢复不透明的映射表在 <see cref="PinShortcuts"/> 里，可单测）。</summary>
    private static PinKey MapKey(Key key) => key switch
    {
        Key.Escape => PinKey.Escape,
        Key.Space => PinKey.Space,
        Key.T => PinKey.T,
        Key.C => PinKey.C,
        Key.R => PinKey.R,
        Key.O => PinKey.O,
        Key.F => PinKey.F,
        Key.Enter or Key.Return => PinKey.Enter,
        Key.D0 or Key.NumPad0 => PinKey.Digit0,
        Key.OemPlus or Key.Add => PinKey.Plus,
        Key.OemMinus or Key.Subtract => PinKey.Minus,
        _ => PinKey.None,
    };

    /// <summary>键盘缩放（Ctrl+加号/减号）：以图片区中心为锚点，与滚轮共用边界反馈。</summary>
    private void StepZoom(int direction)
    {
        var target = PinLayout.StepZoom(
            _zoom, direction * PinLayout.WheelDelta, _settings.PinZoomStep, fine: false, limits: _limits);
        ApplyZoom(target, 0.5, 0.5);
    }

    private void ResetZoom()
    {
        var state = PinToolbarRules.Resolve(PinToolbarAction.ResetZoom, CurrentAvailability);
        if (!state.Enabled)
        {
            ShowToast(state.DisabledReason ?? "当前已是 100%");
            return;
        }

        ApplyZoom(1.0, 0.5, 0.5);
    }

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => ResetZoom();

    private void OnCloseClick(object sender, RoutedEventArgs e) => ClosePin();

    /// <summary>
    /// 双击 = 原文/译文切换（14.3.6 明确不是关闭：误触代价不对称）。落在工具条上的双击交给按钮处理。
    /// </summary>
    private void OnImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ToolbarHost.IsAncestorOf(source))
        {
            return;
        }

        Log.Debug("钉图双击：切换原文/译文（不关闭窗口）");
        ToggleTextLayer(manual: true);
        SwitchTextLayerRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// 关闭：必须 <c>Close()</c>（不是 <c>Hide()</c>）并置空位图与覆盖层引用，
    /// 保证不留残影、位图尽快可回收（14.3.10）。
    /// </summary>
    public void ClosePin()
    {
        _chipTimer.Stop();
        ReleaseContent();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _chipTimer.Stop();
        ReleaseContent();
        base.OnClosed(e);
    }

    private void ReleaseContent()
    {
        ContentImage.Source = null;
        OverlayLayer.Children.Clear();
        _source = null;
        _content = null;
    }

    /// <summary>激活指示：非激活无描边、激活 1 DIP Color.Capture.Selection（14.3.6）。</summary>
    private void UpdateActiveFrame(bool active) =>
        ActiveFrame.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 覆盖层内容（批 4c 只换数据，不动结构） ----------------

    /// <summary>
    /// 按当前换算系数 k 重建覆盖层元素：每个块 = 覆盖面（取样底色，缺失时用 `Brush.Overlay.CoverFallback`）
    /// + 替换文字（浅底配深字、深底配浅字，令牌由 <see cref="PinOverlayRules.TextColorToken"/> 决定）。
    /// 字号与圆角同样乘以 k，因此缩放时块与图片严格对齐。任何异常都不冒泡（失败时图片照常显示）。
    /// </summary>
    private void RebuildOverlay()
    {
        OverlayLayer.Children.Clear();

        var content = _content;
        if (content is null || content.Blocks.Count == 0)
        {
            return;
        }

        try
        {
            var k = PinLayout.ContentScale(_rect.Width, _dpiScale, _imageWidth, _framePhys);
            if (k <= 0)
            {
                return;
            }

            var radius = _coverRadiusDip * k;
            var padding = OverlayLayout.PaddingPx * k; // 与排版阶段同一常量，测量与渲染不会走偏
            var font = (FontFamily)FindResource("Font.App");

            foreach (var block in content.Blocks)
            {
                var element = PinLayout.OverlayElement(block, k);
                var width = element.Width;
                var height = element.Height;
                var left = element.Left;
                var top = element.Top;

                var cover = new Rectangle
                {
                    Width = width,
                    Height = height,
                    RadiusX = radius,
                    RadiusY = radius,
                };

                if (block.CoverArgb is { } argb)
                {
                    cover.Fill = new SolidColorBrush(Color.FromArgb(
                        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                }
                else
                {
                    cover.SetResourceReference(Shape.FillProperty, "Brush.Overlay.CoverFallback");
                }

                Canvas.SetLeft(cover, left);
                Canvas.SetTop(cover, top);
                OverlayLayer.Children.Add(cover);

                if (string.IsNullOrEmpty(block.Text))
                {
                    continue;
                }

                var fontPx = element.FontSize;
                var text = new TextBlock
                {
                    Text = block.Text,
                    FontFamily = font,
                    FontSize = fontPx,
                    Width = Math.Max(1, width - (2 * padding)),
                    // 整块排版（模式 B）的译文是多行且需要换行；原位替换（模式 A）由排版阶段保证单行放得下
                    TextWrapping = block.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = block.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                };
                if (block.Wrap)
                {
                    text.MaxHeight = Math.Max(1, height - (2 * padding));
                }
                text.SetResourceReference(TextBlock.ForegroundProperty, PinOverlayRules.TextColorToken(block.CoverArgb));
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Canvas.SetLeft(text, left + padding);
                Canvas.SetTop(text, top + Math.Max(0, (height - text.DesiredSize.Height) / 2));
                OverlayLayer.Children.Add(text);
            }

            LogOverlayState("构建完成");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图覆盖层构建失败，已忽略（图片仍正常显示）");
        }
    }

    /// <summary>
    /// 覆盖层诊断（<c>--verbose</c> 时可见）：一次打全「块数 / 每块的图像像素矩形 → 窗口内 DIP 矩形 /
    /// 元素数 / 画布尺寸 / 可见性与不透明度」，用于定位「译文没画到图上」这类只在真机复现的问题
    /// （没有绘制 / 画到界外 / 尺寸为 0 / 被裁掉 / 被隐藏，五个可能一次性区分开）。
    /// 只记几何、数量与文本长度，不记译文内容（14.3.10）。
    /// </summary>
    private void LogOverlayState(string stage)
    {
        var blocks = _content?.Blocks ?? [];
        var k = PinLayout.ContentScale(_rect.Width, _dpiScale, _imageWidth, _framePhys);
        var parts = new List<string>(blocks.Count);
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var element = PinLayout.OverlayElement(block, k);
            var outside = element.Width <= 0 || element.Height <= 0
                || element.Left + element.Width <= 0 || element.Top + element.Height <= 0
                || (OverlayLayer.ActualWidth > 0 && element.Left >= OverlayLayer.ActualWidth)
                || (OverlayLayer.ActualHeight > 0 && element.Top >= OverlayLayer.ActualHeight);
            parts.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}:({1},{2},{3},{4})px→({5:0.#},{6:0.#},{7:0.#},{8:0.#})dip f={9:0.#} wrap={10} 文本{11}字{12}",
                index, block.Rect.X, block.Rect.Y, block.Rect.Width, block.Rect.Height,
                element.Left, element.Top, element.Width, element.Height, element.FontSize,
                block.Wrap, block.Text?.Length ?? 0, outside ? " 在画布外" : string.Empty));
        }

        Log.Debug(
            "钉图覆盖层[{Stage}]：k={K:0.####}，层 {Layer}，Opacity={Opacity:0.##}，Visibility={Visibility}，"
            + "元素 {Children} 个，画布 {CanvasW:0.#}×{CanvasH:0.#} DIP（图片区 {ViewW:0.#}×{ViewH:0.#} DIP，"
            + "图像 {ImageW}×{ImageH} px），窗口 {RectW}×{RectH} px；块 [{Blocks}]",
            stage, k, _layer, OverlayLayer.Opacity, OverlayLayer.Visibility, OverlayLayer.Children.Count,
            OverlayLayer.ActualWidth, OverlayLayer.ActualHeight,
            ContentArea.ActualWidth, ContentArea.ActualHeight,
            _imageWidth, _imageHeight, _rect.Width, _rect.Height,
            parts.Count == 0 ? "无" : string.Join(" | ", parts));
    }

    // ---------------- 物理像素摆放 ----------------

    /// <summary>
    /// 重摆放（跨屏 / DPI 变化后调用）：按窗口当前所在屏刷新工作区与缩放比，
    /// 把物理尺寸恢复成「图像像素 × zoom」（DPI 变化会由 WPF 按 DIP 保持尺寸而冲掉它），
    /// 位置保持左上角并钳制进工作区。
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _positioning)
        {
            return;
        }

        var probe = _rect;
        if (ScreenInterop.GetWindowRect(hwnd, out var actual))
        {
            probe = new PhysicalRect(actual.Left, actual.Top, actual.Right - actual.Left, actual.Bottom - actual.Top);
        }

        RefreshEnvironment(probe);
        ApplyPanel(); // 跨屏后条带/面板物理高度随屏缩放变化，必须先刷新再算尺寸与上限
        ApplyChrome();
        _limits = ResolveLimits();
        _zoom = PinLayout.ClampZoom(_zoom, _limits);
        var (width, height) = PinLayout.PhysicalSizeWithChrome(
            _imageWidth, _imageHeight, _zoom, _framePhys, _chromePhysHeight);
        PlaceAndVerify(PinLayout.ClampIntoWork(
            probe with { Width = width, Height = height }, _workArea));
        UpdateScalingMode();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            RebuildOverlay(); // 跨屏后 DIP 换算系数 k 变了，覆盖块必须按新 k 重算才能与图片对齐
            UpdateToolbarState();
        }));
    }

    /// <summary><c>SetWindowPos</c> 物理摆放 + <c>GetWindowRect</c> 校验（不一致按差值纠正一次，不循环）。</summary>
    private void PlaceAndVerify(PhysicalRect rect)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _rect = rect;
        try
        {
            _positioning = true;
            ApplyDipSize(rect);
            Place(hwnd, rect);

            if (!ScreenInterop.GetWindowRect(hwnd, out var actual))
            {
                Log.Warning("GetWindowRect 校验钉图矩形失败，Win32 错误码 {Error}",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }

            var actualWidth = actual.Right - actual.Left;
            var actualHeight = actual.Bottom - actual.Top;
            if (actual.Left == rect.Left && actual.Top == rect.Top
                && actualWidth == rect.Width && actualHeight == rect.Height)
            {
                Log.Debug("钉图物理摆放完成：({Left},{Top},{Width},{Height}) px（zoom {Zoom:0.###}，缩放 {Scale}）",
                    rect.Left, rect.Top, rect.Width, rect.Height, _zoom, _dpiScale);
                return;
            }

            Log.Debug("钉图矩形与期望不一致（实际 {AL},{AT},{AW},{AH} / 期望 {EL},{ET},{EW},{EH}），按差值纠正一次",
                actual.Left, actual.Top, actualWidth, actualHeight, rect.Left, rect.Top, rect.Width, rect.Height);
            Place(hwnd, new PhysicalRect(
                actual.Left + (rect.Left - actual.Left),
                actual.Top + (rect.Top - actual.Top),
                rect.Width,
                rect.Height));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "钉图物理像素摆放失败，保持当前位置");
        }
        finally
        {
            _positioning = false;
        }
    }

    private static void Place(IntPtr hwnd, PhysicalRect rect) =>
        ScreenInterop.SetWindowPos(
            hwnd, ScreenInterop.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            ScreenInterop.SWP_SHOWWINDOW | ScreenInterop.SWP_NOACTIVATE);

    /// <summary>DIP 尺寸 = 物理尺寸 / 所在屏缩放（WPF 会据此把物理尺寸做出来，随后 SetWindowPos 再校准）。</summary>
    private void ApplyDipSize(PhysicalRect rect)
    {
        Width = PinLayout.DipLength(rect.Width, _dpiScale);
        Height = PinLayout.DipLength(rect.Height, _dpiScale);
    }

    /// <summary>
    /// 位图缩放模式（14.3.6）：判据用 DIP 侧系数 k（WPF 是在 DIP 空间缩放位图的）——
    /// k 为整数时用最近邻（像素锐利、文字边缘不糊），否则用 HighQuality
    /// （150% 屏上 zoom = 1.0 的 k = 0.667，用最近邻会明显锯齿）。
    /// </summary>
    private void UpdateScalingMode()
    {
        var k = PinLayout.ContentScale(_rect.Width, _dpiScale, _imageWidth, _framePhys);
        var nearest = Math.Abs(k - Math.Round(k)) < 0.01;
        RenderOptions.SetBitmapScalingMode(
            ContentImage, nearest ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private PinZoomLimits ResolveLimits() =>
        PinLayout.ResolveLimits(
            _settings.PinZoomMin, _settings.PinZoomMax,
            // 阴影外框与条带/面板先占位置：保证「图片 + 条带 (+面板)」整体完整可见（v1.2 ④）
            PinLayout.WorkWithoutChrome(_workArea, _framePhys, _chromePhysHeight),
            _imageWidth, _imageHeight);

    /// <summary>刷新「所在显示器」的工作区与缩放比（取不到时保留上一次的值，绝不抛异常）。</summary>
    private void RefreshEnvironment(PhysicalRect probe)
    {
        var centerX = probe.Left + Math.Max(probe.Width, 1) / 2;
        var centerY = probe.Top + Math.Max(probe.Height, 1) / 2;
        if (!ScreenInterop.TryGetMonitorAt(centerX, centerY, out var info, out var scale))
        {
            Log.Debug("钉图：取显示器信息失败，沿用上一次的工作区与缩放 ({Scale})", _dpiScale);
            return;
        }

        _workArea = new PhysicalRect(
            info.RcWork.Left,
            info.RcWork.Top,
            info.RcWork.Right - info.RcWork.Left,
            info.RcWork.Bottom - info.RcWork.Top);
        _dpiScale = scale;
    }
}
