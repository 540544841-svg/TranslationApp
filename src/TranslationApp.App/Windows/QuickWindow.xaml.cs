using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;
using TranslationApp.Core.Layout;
using TranslationApp.Core.Placement;
using TranslationApp.Core.Settings;
using TranslationApp.Interop;
using TranslationApp.ViewModels;

namespace TranslationApp.Windows;

/// <summary>
/// 鼠标处翻译小窗（FR-002/003/004）：
/// 无边框 + 圆角阴影 + WS_EX_TOPMOST 置顶；
/// <b>定位一律以物理像素进行</b>（FR-025：ScreenInterop.SetWindowPos 摆放 + GetWindowRect 校验，
/// 算法在 Core 的 <see cref="WindowPlacement"/> 纯函数里，可单测），
/// 绝不通过 <c>Window.Left/Top</c> 定位、也不回读它们参与计算（DIP↔物理往返会累积误差）；
/// 失焦隐藏用 EVENT_SYSTEM_FOREGROUND 监听，200ms 延时、期间窗口内点击取消；Pin 固定；Esc 关闭。
///
/// 尺寸模型（FR-026 / 14.2，阶段 5 批 3 改版）：**每次呼出都以设置里的默认宽高为基准**，
/// 自适应只在此基础上按内容增大（宽上限 640、默认值本身可越过 640），**拖拽只影响本次窗口、绝不写回设置**；
/// 尺寸全部经 <see cref="WindowSizePolicy"/> 纯函数计算后，再走 FR-025 的物理像素摆放。
///
/// 焦点策略（FR-002「不破坏用户上下文」与 FR-004「输入框默认焦点」的取舍）：
/// 小窗可被激活以便用户直接打字（否则 WS_EX_NOACTIVATE 会让键盘事件全部流向原应用，输入翻译无法使用）；
/// 呼出时记录原前台窗口，隐藏时若前台仍是本窗口则主动把焦点还原给原窗口，
/// 从而既支持即按即打字，又不破坏用户原有编辑位置。
/// </summary>
public partial class QuickWindow : Window
{
    private const int HideDelayMs = 200; // 失焦隐藏延时（FR-003）
    private const int ShadowMarginDip = WindowPlacement.ShadowMarginDip; // 阴影留白（XAML 根 Border 的 Margin）

    /// <summary>对比模式所需的最小窗口高度（13.4.2，含阴影留白的 DIP 语义）。</summary>
    private const int ComparisonHeightTwoColumns = 400;
    private const int ComparisonHeightManyColumns = 460;

    /// <summary>FR-026：输入/错误行变化的防抖时长（14.2.4 第 3、5 点）。</summary>
    private const int AdaptiveDebounceMs = 400;

    /// <summary>
    /// D-④：译文返回（ResultText）单独使用的更短防抖。译文已参与高度计算、重算链路存在，
    /// 缩短防抖是为了压缩「译文已到但窗口还没长」的可滚动态窗口；其它来源维持 400ms
    /// （仍避免把「翻译中」进度条的高度算进窗口）。
    /// </summary>
    private const int ResultDebounceMs = 120;

    /// <summary>
    /// FR-026：**生长动画**时长（14.2.4 第 2 点）。取品牌「落印」时长 260 ms（Brand.Duration.Stamp）。
    /// 只驱动**高度**：宽度瞬时到位（逐帧改宽会让中文逐帧重折行，见 <see cref="ApplyIntentSize"/>），
    /// 高度只决定「下方多露出几行」——顶对齐的正文行位置不动，整段生长期间零次重折行。
    /// 缓动曲线用 QuinticEase/EaseOut：尾部几乎不动，视觉上「稳稳落定」。
    /// </summary>
    private const int AdaptiveAnimationMs = 260;

    /// <summary>
    /// FR-026：程序写入尺寸后的「屏蔽窗口」。自适应改宽高、其 260 ms 动画、以及随后的物理摆放
    /// 都会触发 <see cref="OnWindowSizeChanged"/>（SizeChanged 是异步的，单纯的 bool 标志盖不住动画的每一帧），
    /// 在此期间不回写意图尺寸（否则会把动画的中间帧当成真实尺寸）。
    /// 取动画时长 + 一帧布局 + 收尾物理摆放的余量（动画 260 ms → 屏蔽 560 ms）。
    /// </summary>
    private const int AdaptiveGuardMs = 560;

    /// <summary>
    /// 译文正文的排版参数：必须与 QuickWindow.xaml 里 ResultBox / MeasureShadow 逐字一致，
    /// 否则量影子的字号比真实排版的字号小，窗口会按「矮一行」生长，最后一行被挤掉。
    /// 设计稿 .dst-text 原值：19px / line-height 1.55。
    /// </summary>
    private const double ResultFontSize = 19;
    private const double ResultLineHeight = 29.5;

    /// <summary>落印总时长（设计稿 @keyframes land 260ms）。</summary>
    private const double StampMs = 260;
    private static readonly TimeSpan StampDuration = TimeSpan.FromMilliseconds(StampMs);

    /// <summary>
    /// 落纸时刻（毫秒）：悬印只占这 82ms（下压越快越重），到这一刻硬压在纸面、同时炸开墨爆与卡片反震；
    /// 飞溅墨点 / 冲击闪光 / 墨气三层观效都以此为 <c>BeginTime</c>。
    /// </summary>
    private const double ImpactMs = 82;

    /// <summary>墨爆的 6 粒飞溅墨点（角度单位度：0=正右、90=正下；距离单位 DIP）。印面标在右上角，
    /// 所以整体往卡片内侧偏：向右只留一粒短程，避免飞出去被卡片右缘裁掉。</summary>
    private static readonly (double AngleDeg, double Distance)[] SealDrops =
        [(85, 62), (128, 78), (175, 86), (212, 60), (248, 38), (35, 22)];

    /// <summary>墨气扩散时长（设计稿 @keyframes inkmist 460ms）。</summary>
    private static readonly TimeSpan InkMistDuration = TimeSpan.FromMilliseconds(460);

    private readonly QuickTranslateViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly NativeMethods.WinEventDelegate _foregroundEventProc;
    private readonly DispatcherTimer _hideTimer;
    private IntPtr _foregroundHook;
    private IntPtr _previousForeground;

    /// <summary>
    /// 意图 DIP 尺寸（**卡片外框，不含阴影留白**）——尺寸的唯一真源。
    /// 物理尺寸 = <c>round((意图 + 2 × ShadowMargin) × 目标屏缩放)</c>。
    /// FR-025 规则 10：摆放只读本字段，绝不回读 <c>Left/Top/ActualWidth</c>（那正是本缺陷的成因）。
    /// </summary>
    private double _intentWidthDip;
    private double _intentHeightDip;

    /// <summary>物理像素摆放进行中：期间 OnDpiChanged / SizeChanged 一律让路，避免冲掉本次摆放。</summary>
    private bool _repositioning;

    /// <summary>
    /// 本次呼出的定位锚点（物理坐标，= 呼出那一刻的鼠标位置）。之后任何尺寸变化（对照/对比增高、
    /// 自适应、动画收尾）都以它重算矩形；不缓存的话每次都会拿"当前光标"重新锚定，
    /// 用户点一下按钮窗口就跟着鼠标跳走（实测反馈）。换一次呼出才重置。
    /// </summary>
    private NativeMethods.POINT? _placementAnchor;

    /// <summary>
    /// 本次呼出的贴边方向（右/左、下/上）：FR-025 的越界翻转只在首次摆放判一次，之后沿用同一侧。
    /// 译文到达后窗口会长高长宽，若每次按新尺寸重判，越过工作区下沿/右沿的那一刻就会整窗翻到
    /// 另一侧——用户看到的就是「执行翻译时小窗位置变换」。换一次呼出才重置。
    /// </summary>
    private PlacementSide _placementSide;

    /// <summary>
    /// 本次呼出锁定的窗口**顶边物理 Y**（物理像素，含阴影留白的窗口外框）：首次物理摆放落地时采信，
    /// 之后任何尺寸变化（译文到达、对比增高、动画收尾）都只向下生长，顶边一动不动。
    ///
    /// 为什么必须有：<see cref="WindowPlacement.Compute"/> 在「窗口放不下」时会把 y 往上钳制，
    /// 于是译文越长、顶边被顶得越高——用户看到的就是「拉长跳变」。配合
    /// <see cref="AvailableWindowHeightDip"/> 限高（目标高度永远在「顶边到工作区下沿」以内），
    /// 钳制永远不会触发，顶边全程不动。换一次呼出（<see cref="ShowInternal"/>）才重置。
    /// </summary>
    private int? _pinnedTopPhysical;

    /// <summary>已安排过一次 DispatcherPriority.Loaded 重摆放（防重入，参照遮罩窗口的 _dpiHopPending）。</summary>
    private bool _dpiHopPending;

    /// <summary>进入对比模式前的意图 DIP 高度（退出时按内容重算，13.4.2）。</summary>
    private double? _heightBeforeCompare;

    /// <summary>
    /// FR-026：本次会话（一次呼出到一次隐藏）已到达的高度（**窗口 DIP，含阴影留白**），
    /// 用于会话内单调不减。呼出时重置为设置的默认高度，因此「只增不减」只作用于会话内部，
    /// 也不会把上次会话的高度带过来。
    /// </summary>
    private double _sessionHeightDip;

    /// <summary>FR-026：程序写入尺寸后的屏蔽截止时刻（见 <see cref="AdaptiveGuardMs"/>）。</summary>
    private long _adaptiveGuardUntil;

    /// <summary>
    /// FR-026 缺陷修复：本次呼出的**首次物理摆放是否已落地**。
    /// <c>Show()</c> 期 WPF 会按 DIP 建窗并触发一次 <see cref="OnWindowSizeChanged"/>（程序行为，不是用户拖拽），
    /// 此时窗口的 DIP 尺寸与「意图尺寸 + 2×阴影留白」可能还不是同一套值，
    /// 若被当成拖拽写回 <c>_intentWidthDip</c>，随后的定位就会按偏小的尺寸计算（每次全新进程的首次呼出偏小 24 DIP）。
    /// 每次呼出重置为 false，首次 <see cref="PlaceAndVerify"/> 落地后置 true。
    /// </summary>
    private bool _firstPlacementDone;

    /// <summary>FR-026：输入/错误行变化的 400 ms 防抖计时器（避免每敲一个字符就改窗口尺寸）。</summary>
    private readonly DispatcherTimer _adaptiveTimer;

    /// <summary>当前挂起的防抖时长（毫秒），<c>0</c> 表示没有挂起。用于「更早的到期时间赢」的合并，
    /// 防止同一批属性变更里后到的长防抖把译文返回的短防抖顶掉（见 <see cref="WindowSizePolicy.MergeDebounceMs"/>）。</summary>
    private int _adaptivePendingMs;

    /// <summary>动画期间译文框被临时关掉的滚动条可见性（原值）。</summary>
    private ScrollBarVisibility? _frozenResultScrollBar;

    /// <summary>
    /// 译文已落定、但还没到播放时机（用户要求「弹窗大小与文字排版全部执行完后再落印」）。
    /// <see cref="OnViewModelPropertyChanged"/> 收到 ResultText 时置位，由 <see cref="FlushPendingSeal"/>
    /// 在「尺寸无需变化」与「生长动画收尾」两条出口消费；隐藏小窗 / 重新呼出时清零。
    /// </summary>
    private bool _pendingSeal;

    // 注：小窗顶栏不放「截图翻译」入口。设计稿 .qw-strip-r 只有朗读原文 / 固定常显 /
    // 收起三枚 btn-seal；截图走全局热键与托盘菜单，与小窗「呼出即用」的定位无关。
    /// <summary>FR-040：小窗内 Ctrl+V 请求「粘贴即译」（参数 = 剪贴板文本，可能为 null）；由 App 接清洗与翻译。</summary>
    public event EventHandler<string?>? PasteTranslateRequested;

    public QuickWindow(QuickTranslateViewModel vm, AppSettings settings, ISettingsStore store)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _store = store;
        _foregroundEventProc = OnForegroundEvent; // 字段保活，防止委托被 GC
        DataContext = _vm;

        // FR-026：设置里记录的是「窗口 DIP 尺寸（含阴影留白，与旧版语义一致）」，
        // 即 WindowSizePolicy 的坐标系；意图尺寸（卡片外框）取其减去两侧留白。
        var (defaultWidth, defaultHeight) = DefaultWindowSizeDip();
        _sessionHeightDip = defaultHeight;
        _intentWidthDip = ToIntentWidth(defaultWidth);
        _intentHeightDip = ToIntentHeight(defaultHeight);
        SyncSizePropertiesFromIntent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
        _hideTimer.Tick += OnHideTimerTick;

        _adaptiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AdaptiveDebounceMs) };
        _adaptiveTimer.Tick += OnAdaptiveTimerTick;

        // FR-020：对比模式需临时增高（13.4.2）；重新激活时检测对比引擎集合是否变更（13.4.1）
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnWindowActivated;
        SizeChanged += OnWindowSizeChanged;
        LocationChanged += OnWindowLocationChanged; // 用户挪窗后把顶边重钉到用户放的位置（见该方法的说明）
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_TOPMOST));
    }

    /// <summary>
    /// FR-025 步骤 9（关键，不可省）：PerMonitorV2 下窗口跨屏会触发本回调，WPF 会**按 DIP 保持尺寸**
    /// （物理尺寸被改写）从而冲掉 SetWindowPos 的摆放。摆放期间直接让路，其余情况安排一次
    /// Loaded 优先级的重摆放（此时布局已完成，重摆放不会被后续布局覆盖）。
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Log.Debug("小窗 DPI 变化：{Old} → {New}（物理摆放中={Repositioning}）",
            oldDpi.PixelsPerDip, newDpi.PixelsPerDip, _repositioning);

        if (_repositioning)
        {
            return;
        }

        ScheduleDeferredReposition();
    }

    /// <summary>安排一次 Loaded 优先级的物理像素重摆放（防重入；已安排则忽略）。</summary>
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

    /// <summary>印面条来源标签（设计稿 .qw-tag）的四个取值，与设计稿小窗状态机一致。</summary>
    public const string OriginManual = "手动";

    /// <summary>划词取词链路（FR-005）。</summary>
    public const string OriginSelection = "划词";

    /// <summary>剪贴板链路（FR-017 监听 / FR-040 粘贴即译）。</summary>
    public const string OriginClipboard = "剪贴板";

    /// <summary>截图翻译链路（FR-021）。</summary>
    public const string OriginDocument = "文档";

    /// <summary>
    /// 输入翻译热键（FR-004）：窗口已可见时再次触发 = 收起，否则呼出。
    /// 收起逻辑只用于输入热键；划词失败提示等需要「总是显示」的场景请用 ShowForInput。
    /// </summary>
    public void ToggleForInput()
    {
        if (IsVisible && !_vm.IsPinned)
        {
            HideWindow();
            return;
        }

        ShowForInput();
    }

    /// <summary>
    /// 呼出小窗并显示提示（总是显示，不收起）。
    /// 划词取词失败降级、划词结果展示都走这里，避免「窗口已打开时提示反被收起」。
    /// </summary>
    public void ShowForInput(string? notice = null) =>
        ShowInternal(inputText: "", notice: notice, autoTranslate: false, origin: OriginManual);

    /// <summary>呼出小窗（划词翻译模式，FR-005）：带入取到的文本并立即翻译；cleaned 标记本次文本经过阅读清洗。</summary>
    /// <param name="origin">来源标签：划词链路默认「划词」，剪贴板链路传 <see cref="OriginClipboard"/>。</param>
    public void ShowForSelection(string capturedText, string? notice = null, bool cleaned = false,
        string origin = OriginSelection)
    {
        ShowInternal(inputText: capturedText, notice: notice, autoTranslate: true, selectionSource: true, origin: origin);
        if (cleaned)
        {
            _vm.MarkCleaned();
        }
    }

    /// <summary>
    /// FR-021 截图翻译交付（13.2.3 步骤 8）：识别文本进入可编辑输入框（光标置末尾），
    /// 已有输入内容时追加到末尾；sourceLanguage 为按 OCR 语言映射出的翻译源语言（null = 保持自动检测）；
    /// 是否立即翻译由 OcrAutoTranslate 决定，识别语言与目标语言相同时跳过并提示（13.2.5 规则 2）。
    /// </summary>
    public void ShowForOcrText(
        string ocrText, string? sourceLanguage, string? previousInput, string? notice = null)
    {
        var combined = string.IsNullOrWhiteSpace(previousInput)
            ? ocrText
            : string.IsNullOrWhiteSpace(ocrText)
                ? previousInput
                : previousInput.TrimEnd() + "\n" + ocrText;

        var skipTranslation = !string.IsNullOrEmpty(sourceLanguage)
                              && string.Equals(sourceLanguage, _settings.TargetLanguage, StringComparison.OrdinalIgnoreCase);
        var autoTranslate = _settings.OcrAutoTranslate
                            && !skipTranslation
                            && !string.IsNullOrWhiteSpace(ocrText);

        ShowInternal(inputText: combined, notice: notice, autoTranslate: autoTranslate,
            sourceLanguage: sourceLanguage, origin: OriginDocument);

        if (skipTranslation && !string.IsNullOrWhiteSpace(ocrText))
        {
            _vm.StatusText = "识别语言与目标语言相同，已跳过翻译";
            Log.Debug("OCR 识别语言与目标语言相同（{Language}），已跳过翻译", sourceLanguage);
        }
    }

    /// <summary>
    /// 截图前收起小窗（避免把小窗自身截进画面）。
    /// 返回输入框现有文本：仅当小窗原本可见（用户正在编辑）时才带回，识别结果会追加到其后；
    /// 小窗本就隐藏时不带旧内容，避免把上一次会话的文本拼进本次识别结果。
    /// </summary>
    public string PrepareForCapture()
    {
        if (!IsVisible)
        {
            return "";
        }

        var input = _vm.InputText ?? "";
        HideWindow();
        return input;
    }

    private void ShowInternal(
        string inputText, string? notice, bool autoTranslate,
        string? sourceLanguage = null, string origin = OriginManual,
        bool selectionSource = false)
    {
        _previousForeground = NativeMethods.GetForegroundWindow();
        Log.Debug("小窗显示：原前台={Previous}，自动翻译={Auto}", _previousForeground, autoTranslate);

        _vm.ResetForShow(notice, _settings.TargetLanguage, inputText, sourceLanguage, origin);
        // FR-016：仅划词会话在翻译成功后自动朗读原文；手动输入/OCR 会话不朗读
        _vm.SetSelectionSession(selectionSource);
        // FR-058：告诉 VM 用户当时在哪个程序里（只给进程名）；必须在 ResetForShow 之后，
        // 否则命中的语言对会被重置覆盖
        _vm.SetForegroundApp(ForegroundAppProbe.ProcessNameOf(_previousForeground));
        // FR-026（14.2.4 时机 1）：**每次呼出都回到设置的默认宽高**（清掉上次会话的拖拽结果），
        // 并在**定位之前**按当前内容算一次尺寸，否则这次定位用的还是上一次会话的旧尺寸
        // （这是与 FR-025 的耦合点）。呼出时不做动画：窗口尚未显示，动画可能不计时导致尺寸落不到目标值。
        // 会话级定位状态一律在「按内容算尺寸之前」重置：锚点决定这次摆在哪、贴边方向决定往哪边长、
        // 顶边锁定决定这次能长多高，任何一项残留都会把本次的尺寸上限算错（包括呼出这一次）。
        _placementAnchor = null;             // 重新按当前鼠标位置锚定一次，之后本次会话内不再变
        _placementSide = PlacementSide.Auto; // 贴边方向重新判一次
        _pinnedTopPhysical = null;           // 顶边锁定交给本次呼出的首次物理摆放落地

        _sessionHeightDip = DefaultWindowSizeDip().Height;
        _pendingSeal = false; // 新会话：上一会话残留下来的落印登记一律作废
        ApplyAdaptiveSize("呼出", animate: false);

        // FR-026 缺陷修复：Show 期由 WPF 触发的 SizeChanged 一律视为程序行为——
        // ① 首次物理摆放完成前不回写意图尺寸（见 _firstPlacementDone）；
        // ② 尺寸屏蔽窗口从这里开始计时（applyAdaptiveSize 因「尺寸无变化」提前返回时，
        //    它是唯一能盖住 Show 期那次 SizeChanged 的守卫）。
        _firstPlacementDone = false;
        _adaptiveGuardUntil = Environment.TickCount64 + AdaptiveGuardMs;

        // FR-025：① Show 前先摆一次（避免在旧位置闪现）——首次呼出时窗口还没有 HWND，
        // 必须先建句柄并提前把 DPI 上下文切到目标屏（见 OrderHandleOntoTargetMonitor），
        // 否则既有「在旧位置闪现」也有「先大一号再跳回」；
        // ② Show 后 WPF 会按 DIP 应用自身几何，再摆一次覆盖它
        // ③ 兜底再安排一次 Loaded 优先级重摆放（跨屏 DPI 跳变 / 布局后的尺寸变化）
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero)
        {
            OrderHandleOntoTargetMonitor();
        }

        Reposition();
        Show();
        Reposition();
        ScheduleDeferredReposition();
        ForceActivate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        HookForeground();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            InputBox.Focus();
            var focused = Keyboard.Focus(InputBox);
            InputBox.CaretIndex = InputBox.Text.Length;
            Log.Debug("输入框聚焦结果：键盘焦点={Focused}，焦点元素={Element}",
                focused is not null, Keyboard.FocusedElement?.GetType().Name ?? "null");
        }));

        if (autoTranslate && !string.IsNullOrWhiteSpace(inputText))
        {
            _vm.TranslateCommand.Execute(null);
        }
    }

    /// <summary>
    /// 确保窗口获得键盘焦点：热键触发时本进程持有最后输入事件，通常可直接激活；
    /// 若被系统前台锁拒绝，则临时附加前台线程输入队列后再试一次。
    /// </summary>
    private void ForceActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Activate();
        SetForegroundFocus();

        var hwnd2 = new WindowInteropHelper(this).Handle;
        // 全程物理像素：这里记 GetWindowRect 的实际矩形，**不读 Window.Left/Top**（DIP 值，且与 FR-025 的
        // 「不回读 WPF 几何」约定冲突；这两者本库内无任何计算用途，只作诊断）
        var rectText = ScreenInterop.GetWindowRect(hwnd2, out var rect)
            ? $"({rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}) px"
            : "未知";
        Log.Debug("激活结果：前台={Foreground}，本窗口={Hwnd}，物理矩形={Rect}",
            NativeMethods.GetForegroundWindow(), hwnd2, rectText);

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return;
        }

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), IntPtr.Zero);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        if (NativeMethods.AttachThreadInput(foregroundThread, currentThread, true))
        {
            try
            {
                SetForegroundFocus();
            }
            finally
            {
                NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
    }

    private void SetForegroundFocus()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        Focus();
    }

    /// <summary>
    /// 首次呼出的建窗：取得 HWND 后把窗口**只移动不改尺寸**地落到目标屏，让 DPI 上下文在 <c>Show()</c> 之前
    /// 就切到鼠标所在屏（FR-025 步骤 9 的同一陷阱）。
    /// <para>
    /// 为什么不能直接一步摆到最终物理矩形：跨 DPI 摆放时 Windows 会按「DPI 比例 × 窗口**当时的**物理矩形」
    /// 给出建议矩形，而 WPF 会照它改窗口尺寸——若此时窗口已经是最终物理尺寸，就会被再放大一个 DPI 比例，
    /// 表现为首次呼出先出现约 1.5 倍大小的一闪、随后才被摆正。
    /// 只移动时窗口还是 WPF 的默认几何（物理尺寸 = 意图尺寸 × 原屏缩放），按 DPI 比例换算后恰好等于
    /// 目标屏上的最终物理尺寸；因此这一步之后尺寸天然正确，随后的 <see cref="Reposition"/> 只是复核位置。
    /// </para>
    /// </summary>
    private void OrderHandleOntoTargetMonitor()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (hwnd == IntPtr.Zero)
        {
            Log.Debug("小窗首次建窗：未能取得窗口句柄，交由随后的摆放处理");
            return;
        }

        if (!TryGetPlacementInput(hwnd, out var cursor, out var work, out var dpi))
        {
            Log.Debug("小窗首次建窗：取不到目标屏信息，交由随后的摆放处理");
            return;
        }

        var rect = ComputePhysicalRect(cursor, work, dpi);
        ScreenInterop.SetWindowPos(
            hwnd, ScreenInterop.HWND_TOPMOST, rect.Left, rect.Top, 0, 0,
            ScreenInterop.SWP_NOSIZE | ScreenInterop.SWP_NOACTIVATE);
        Log.Debug("小窗首次建窗：已只移动不改尺寸地落到目标屏 ({Left},{Top})，目标屏 DPI {Dpi}",
            rect.Left, rect.Top, dpi);
    }

    /// <summary>
    /// 以**物理像素**摆放小窗（FR-025 / 14.1.2），任何尺寸变化（对比模式增高 / 自适应 / 用户拖拽）后都必须调用。
    /// 流程：取鼠标物理坐标 → 鼠标所在屏工作区 → 目标屏缩放 → 物理尺寸 → WindowPlacement 纯函数算矩形 →
    /// SetWindowPos → GetWindowRect 校验（不一致按差值纠正一次）→ GetDpiForWindow 复核（不符则按实际 DPI 重算摆一次）。
    /// 任一步失败只记日志并保持当前位置，不抛异常。
    /// </summary>
    public void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _repositioning = true;
            try
            {
                RepositionCore(hwnd, dpiOverride: null);
            }
            finally
            {
                _repositioning = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "小窗物理像素定位失败，保持当前位置");
        }
    }

    private void RepositionCore(IntPtr hwnd, uint? dpiOverride)
    {
        if (!TryGetPlacementInput(hwnd, out var cursor, out var work, out var monitorDpi))
        {
            Log.Warning("小窗定位降级：取鼠标坐标或显示器信息失败，保持当前位置");
            return;
        }

        // 步骤 8 的复核会传入窗口实际 DPI：只影响物理尺寸的换算，位置仍以鼠标所在屏为准
        var dpi = dpiOverride ?? monitorDpi;
        var rect = ComputePhysicalRect(cursor, work, dpi);
        var placed = PlaceAndVerify(hwnd, rect, dpi);
        if (dpiOverride is null && _pinnedTopPhysical is null)
        {
            // 本次呼出的首次物理摆放已落地 → 顶边就此钉住（后续尺寸变化只向下生长）
            _pinnedTopPhysical = placed.Top;
            Log.Debug("顶边锁定：本次呼出顶边 = {Top} px（物理）", placed.Top);
        }

        if (dpiOverride.HasValue)
        {
            return; // 复核只做一次，之后不再循环
        }

        var actualDpi = NativeMethods.GetDpiForWindow(hwnd);
        if (actualDpi == 0 || actualDpi == monitorDpi)
        {
            return;
        }

        Log.Debug("小窗实际 DPI {Actual} ≠ 目标屏 DPI {Target}（窗口尚未落到目标屏），按实际 DPI 重算尺寸后再摆一次",
            actualDpi, monitorDpi);
        RepositionCore(hwnd, actualDpi);
    }

    /// <summary>取定位输入：鼠标物理坐标 + 鼠标所在屏工作区（物理像素、虚拟桌面坐标系）+ 目标屏 DPI。</summary>
    private bool TryGetPlacementInput(
        IntPtr hwnd, out NativeMethods.POINT cursor, out NativeMethods.RECT work, out uint dpi)
    {
        cursor = default;
        work = default;
        dpi = 96;

        // 锚点在本次呼出内是固定的（见 _placementAnchor 注释）：只有新的一次呼出才重新读鼠标位置，
        // 否则内容一变高就按"当前光标"重新锚定，用户点按钮时窗口会自己跳走。
        if (_placementAnchor is { } anchor)
        {
            cursor = anchor;
        }
        else
        {
            // 步骤 1：GetPhysicalCursorPos，失败回退 GetCursorPos（PerMonitorV2 下两者同为物理坐标）
            if (!NativeMethods.GetPhysicalCursorPos(ref cursor) && !NativeMethods.GetCursorPos(ref cursor))
            {
                return false;
            }

            _placementAnchor = cursor;
        }

        // 步骤 2：MONITOR_DEFAULTTONEAREST：鼠标落在屏间缝隙时也能取到最近屏
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        work = info.RcWork;

        // 步骤 3：目标屏缩放，失败退 GetDpiForWindow，再失败按 96（scale = 1.0）
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX != 0)
        {
            dpi = dpiX;
            return true;
        }

        var windowDpi = NativeMethods.GetDpiForWindow(hwnd);
        dpi = windowDpi != 0 ? windowDpi : 96u;
        Log.Debug("GetDpiForMonitor 失败，退用 GetDpiForWindow：{Dpi}", dpi);
        return true;
    }

    /// <summary>步骤 4~5：物理尺寸由意图 DIP 尺寸 × 目标屏缩放得出，再交纯函数按工作区收缩 + 定位。</summary>
    private PhysicalRect ComputePhysicalRect(NativeMethods.POINT cursor, NativeMethods.RECT work, uint dpi)
    {
        var scale = dpi / 96.0;

        var wanted = new PhysicalRect(
            0,
            0,
            WindowPlacement.ToPhysicalLength(_intentWidthDip + ShadowMarginDip * 2, scale),
            WindowPlacement.ToPhysicalLength(_intentHeightDip + ShadowMarginDip * 2, scale));

        var workRect = new PhysicalRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        var fitted = WindowPlacement.FitToWorkArea(
            wanted,
            workRect,
            WindowPlacement.ToPhysicalLength(SafeMin(MinWidth) + ShadowMarginDip * 2, scale),
            WindowPlacement.ToPhysicalLength(SafeMin(MinHeight) + ShadowMarginDip * 2, scale));

        var gapPhysical = WindowPlacement.ToPhysicalLength(WindowPlacement.CursorGapDip, scale);
        // 首次摆放定下本次呼出的贴边方向；之后（译文到达、对比增高、动画收尾）只钳制、不翻边
        if (_placementSide.IsAuto)
        {
            _placementSide = WindowPlacement.DecideSide(cursor.X, cursor.Y, workRect, fitted, gapPhysical);
        }
        var placed = WindowPlacement.Compute(cursor.X, cursor.Y, workRect, fitted, gapPhysical, _placementSide);
        if (_pinnedTopPhysical is not { } pinnedTop)
        {
            return placed; // 本次呼出还没落过盘：顶边以首次摆放为准（见 RepositionCore）
        }

        // 锁上边沿（14.1.3 补充）：顶边已被本次呼出钉住，尺寸变化只改宽高、顶边一概不动（只向下生长）
        var locked = WindowPlacement.PinTopEdge(placed, workRect, pinnedTop);
        if (locked.Top != pinnedTop)
        {
            // 只有「顶边 + 高度」确实装不下才让步：新位置就是新锚点，避免下次又弹回去
            _pinnedTopPhysical = locked.Top;
            Log.Debug(
                "顶边锁定让位：{Pinned} → {Locked} px（高度 {Height} 放不下）", pinnedTop, locked.Top, locked.Height);
        }

        return locked;
    }

    /// <summary>步骤 6~7：SetWindowPos 摆放 → GetWindowRect 校验，不一致按差值纠正一次（只允许一次，避免死循环）。</summary>
    /// <returns>最终交给 <c>SetWindowPos</c> 的那个矩形（顶边锁定以它为准）。</returns>
    private PhysicalRect PlaceAndVerify(IntPtr hwnd, PhysicalRect rect, uint targetDpi)
    {
        var scale = targetDpi / 96.0;

        // 尺寸被工作区收缩时，按文档把 DIP 意图尺寸同步写回（意图 = physW/scale - 2×阴影留白）
        var widthDip = Math.Max(SafeMin(MinWidth), Math.Round(rect.Width / scale - ShadowMarginDip * 2, 1));
        var heightDip = Math.Max(SafeMin(MinHeight), Math.Round(rect.Height / scale - ShadowMarginDip * 2, 1));
        if (Math.Abs(widthDip - _intentWidthDip) > 0.5 || Math.Abs(heightDip - _intentHeightDip) > 0.5)
        {
            _intentWidthDip = widthDip;
            _intentHeightDip = heightDip;
            SyncSizePropertiesFromIntent();
            Log.Information(
                "工作区放不下小窗，已收缩到工作区尺寸：意图 {IntentW}×{IntentH} DIP，物理 {PhysW}×{PhysH} px（缩放 {Scale}）",
                _intentWidthDip, _intentHeightDip, rect.Width, rect.Height, scale);

            // WPF 会因 Width/Height 变化重排并可能再改写 HWND 尺寸，安排一次重摆放兜底
            ScheduleDeferredReposition();
        }

        Place(hwnd, rect);
        _firstPlacementDone = true; // 首次物理摆放已落地（此后 SizeChanged 才代表窗口真实尺寸变化）
        if (!ScreenInterop.GetWindowRect(hwnd, out var actual))
        {
            Log.Warning("GetWindowRect 校验小窗矩形失败，Win32 错误码 {Error}",
                System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return rect;
        }

        var actualWidth = actual.Right - actual.Left;
        var actualHeight = actual.Bottom - actual.Top;
        if (actual.Left == rect.Left && actual.Top == rect.Top
            && actualWidth == rect.Width && actualHeight == rect.Height)
        {
            // 供真机复验（AC 1~5）：每屏连续呼出 5 次的这行日志应当逐像素一致
            Log.Debug("小窗物理摆放完成：({Left},{Top},{Width},{Height}) px（缩放 {Scale}，DPI {Dpi}）",
                rect.Left, rect.Top, rect.Width, rect.Height, scale, targetDpi);
            return rect;
        }

        Log.Debug(
            "小窗矩形与期望不一致（实际 {AL},{AT},{AW},{AH} / 期望 {EL},{ET},{EW},{EH}），按差值纠正一次",
            actual.Left, actual.Top, actualWidth, actualHeight, rect.Left, rect.Top, rect.Width, rect.Height);

        Place(hwnd, new PhysicalRect(
            actual.Left + (rect.Left - actual.Left),
            actual.Top + (rect.Top - actual.Top),
            rect.Width,
            rect.Height));

        if (ScreenInterop.GetWindowRect(hwnd, out var after)
            && (after.Left != rect.Left || after.Top != rect.Top
                || after.Right - after.Left != rect.Width || after.Bottom - after.Top != rect.Height))
        {
            Log.Debug("纠正后仍不一致（实际 {AL},{AT},{AW},{AH}）——只纠正一次，避免死循环",
                after.Left, after.Top, after.Right - after.Left, after.Bottom - after.Top);
        }

        return rect;
    }

    private static void Place(IntPtr hwnd, PhysicalRect rect) =>
        ScreenInterop.SetWindowPos(
            hwnd, ScreenInterop.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            ScreenInterop.SWP_NOACTIVATE);

    /// <summary>
    /// 用户拖拽边缘导致窗口 DIP 尺寸变化时更新意图尺寸——**只影响本次窗口**：
    /// 既不写回设置（下次呼出仍回到默认宽高），也不改变自适应模式
    /// （用户可在小窗空白处右键「设为默认尺寸」把当前尺寸固化为默认值）。
    /// 程序自身的尺寸变化（物理摆放、自适应与动画）由 <c>_repositioning</c> / <c>_dpiHopPending</c> / 屏蔽窗口排除。
    /// </summary>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_repositioning || _dpiHopPending || !_firstPlacementDone
            || Environment.TickCount64 < _adaptiveGuardUntil)
        {
            return;
        }

        // 物理摆放后 WPF 会按实际物理尺寸回读一次（意图尺寸 = 实际 DIP − 2×阴影留白），
        // 这类回读与用户拖拽同样只需把意图尺寸对齐即可，无需区分
        if (e.WidthChanged)
        {
            _intentWidthDip = ToIntentWidth(e.NewSize.Width);
        }

        if (e.HeightChanged)
        {
            _intentHeightDip = ToIntentHeight(e.NewSize.Height);
        }
    }

    /// <summary>
    /// 用户把窗挪走后（拖拽顶栏、拖拽边缘）把顶边重钉到他放的位置：
    /// 下一次按内容生长就从「他放的地方」继续向下长，而不是弹回呼出时那个顶边。
    /// 程序自身的物理摆放与 Show 期 WPF 恢复位置也会发 WM_MOVE，用
    /// <c>_repositioning</c> / <c>_firstPlacementDone</c> / 尺寸屏蔽窗口（<see cref="AdaptiveGuardMs"/>）排除。
    /// </summary>
    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_repositioning || !_firstPlacementDone || Environment.TickCount64 < _adaptiveGuardUntil)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && ScreenInterop.GetWindowRect(hwnd, out var actual))
        {
            _pinnedTopPhysical = actual.Top;
        }
    }

    /// <summary>
    /// FR-026：把当前窗口尺寸写入默认宽高设置（小窗空白处右键菜单）——
    /// 下次呼出即以该尺寸为基准，自适应仍只会在此基础上按内容增大。
    /// </summary>
    private void OnSetDefaultSizeClick(object sender, RoutedEventArgs e)
    {
        var (width, height) = WindowSizePolicy.NormalizeAsDefault(
            ToWindowWidth(_intentWidthDip), ToWindowHeight(_intentHeightDip));

        _settings.QuickWindowWidth = width;
        _settings.QuickWindowHeight = height;
        _store.Save(_settings);
        _sessionHeightDip = height;

        // 复用现有状态行做一次性反馈（不新增控件），与设置卡片里的说明文案对应
        _vm.StatusText = $"已设为默认尺寸：{width:0} × {height:0}";
        Log.Information("已将当前小窗尺寸设为默认：{Width}×{Height} DIP", width, height);
    }

    private void OnAdaptiveTimerTick(object? sender, EventArgs e)
    {
        _adaptiveTimer.Stop();
        _adaptivePendingMs = 0;
        RecomputeAdaptiveSize("内容变化（防抖后）");
    }

    /// <summary>
    /// 重算并落地窗口尺寸（FR-026 / 14.2.4 的 5 个触发时机的公共出口）。
    /// 会先判断对比状态，因此调用方无需重复判断；窗口未显示时也安全（呼出时尚未 Show 就要算）。
    /// </summary>
    private void RecomputeAdaptiveSize(string reason)
    {
        if (!IsVisible)
        {
            _pendingSeal = false; // 已隐藏：本次会话作废，落印不再补播
            return; // 已隐藏：留到下次呼出按默认值与新内容重算
        }

        ApplyAdaptiveSize(reason, animate: true);
    }

    /// <summary>
    /// 按设置基准 + 内容测量算出最终宽高并落地（14.2.1 第 5 点：固定外框 + 输入区实测高 + 译文区实测高 +
    /// 状态/错误行实测高，再按 14.2.3 夹取；宽度按译文所需最长行加宽、上限 640）。
    /// 最终尺寸完全由纯函数 <see cref="WindowSizePolicy"/> 决定：基准始终是设置里的默认宽高。
    /// </summary>
    private void ApplyAdaptiveSize(string reason, bool animate)
    {
        if (_vm.IsComparing)
        {
            _pendingSeal = false; // 对比模式不播落印（分栏结果各有各的落定）
            return; // 对比模式下暂停自适应（结果区已是分栏，不参与测量），尺寸由 ExpandForComparison 负责
        }

        var adaptive = !WindowSizePolicy.IsManual(_settings.QuickWindowSizeMode);
        var (defaultWidth, defaultHeight) = DefaultWindowSizeDip();
        double targetWidth = defaultWidth;
        double targetHeight = defaultHeight;

        try
        {
            if (adaptive)
            {
                // 不限宽宽度按「目标行数」折算成宽度需求：直接用不限宽宽度会让稍长文本立刻顶到上限，
                // 变成两档跳；折算后宽度随内容量渐进增长（见 WindowSizePolicy.RequiredWidthForLineTarget）。
                var neededWidth = WindowSizePolicy.RequiredWidthForLineTarget(MeasureNeededWidthDip());
                // 再按「顶边锁定后剩下的空间」收口：宽度与高度都不能越出本次呼出的锚定边，
                // 否则加宽/加高后会被 Compute 猛地钳回工作区内 —— 那就是用户看到的跳变。
                targetWidth = CapByAvailableSpan(
                    WindowSizePolicy.ResolveWidth(defaultWidth, neededWidth, adaptToContent: true),
                    defaultWidth,
                    AvailableWindowWidthDip());

                // 宽度先定下来，高度按「新宽度下的内容」测量，避免用旧宽度测出的高度
                var neededHeight = MeasureContentHeightDip(ContentWidthFor(targetWidth));
                targetHeight = CapByAvailableSpan(
                    WindowSizePolicy.ResolveHeight(
                        defaultHeight, neededHeight, _sessionHeightDip, adaptToContent: true, CurrentWorkAreaHeightDip()),
                    defaultHeight,
                    AvailableWindowHeightDip());
                _sessionHeightDip = targetHeight; // 单调不减的基线随之前移
            }
            else
            {
                // 自适应关闭：**不做任何内容测量**，严格使用默认宽高（需求 5）
                _sessionHeightDip = defaultHeight;
            }
        }
        catch (Exception ex)
        {
            // 14.2.4 第 5 点：测量失败保持当前尺寸，不缩小、不抛异常
            Log.Debug(ex, "自适应尺寸计算失败（{Reason}），保持当前尺寸", reason);
            FlushPendingSeal(); // 尺寸保持现状：译文已排版，不必把落印也一起丢掉
            return;
        }

        var intentWidth = ToIntentWidth(targetWidth);
        var intentHeight = ToIntentHeight(targetHeight);
        if (Math.Abs(intentWidth - _intentWidthDip) < 0.5 && Math.Abs(intentHeight - _intentHeightDip) < 0.5)
        {
            FlushPendingSeal(); // 尺寸无需变化：译文此刻已是终态排版，落印立即补上（短译文场景）
            return; // 尺寸无变化：保持现状（也避免无谓的物理摆放）
        }

        Log.Debug("小窗尺寸（{Reason}，自适应={Adaptive}）：{Width:F1} × {Height:F1} DIP",
            reason, adaptive, targetWidth, targetHeight);

        // 从这里开始的尺寸变化（含随后的布局、物理摆放、动画）一律视为程序引起，不回写意图尺寸
        _adaptiveGuardUntil = Environment.TickCount64 + AdaptiveGuardMs;
        _intentWidthDip = intentWidth;
        _intentHeightDip = intentHeight;
        ApplyIntentSize(animate);
    }

    /// <summary>
    /// 把意图尺寸落到窗口上（14.2.4「不能抖动」）：**宽度瞬时到位、只对高度做动画**。
    /// 宽度逐帧变化会让中文逐帧重折行（正是用户看到的「文字排版跳变」）；宽度瞬时到终态后，
    /// 正文从第一帧起就是终态排版，动画只把「下方多出来的行」逐帧揭开 —— 顶对齐的正文一个像素都不动。
    /// 动画期间窗口顶边由 <see cref="_pinnedTopPhysical"/> 锁住——WPF 改 Height 时保持 Top 不变，窗口只会向下长；
    /// 动画期间不 SetWindowPos（会与动画争尺寸），播完再摆一次让物理像素与工作区对齐（14.1.2 第 11 步），
    /// 并在这之后才补播落印（用户要求：尺寸与排版全部执行完再落印）。
    /// </summary>
    private void ApplyIntentSize(bool animate)
    {
        var fromWidth = Width;
        var fromHeight = Height;
        var targetWidth = ToWindowWidth(_intentWidthDip);   // WPF 的宽高是**含阴影留白的窗口 DIP**，不是卡片意图尺寸
        var targetHeight = ToWindowHeight(_intentHeightDip);

        // 基值先落到目标：动画只是视觉过渡，任何时刻撤掉动画都停在目标值
        SyncSizePropertiesFromIntent();

        var widthMoved = Math.Abs(fromWidth - targetWidth) >= 0.5;
        var heightMoved = Math.Abs(fromHeight - targetHeight) >= 0.5;

        // 宽度不做动画：撤掉可能残留的宽度动画，立即落到目标宽度（见方法说明的折行理由）。
        if (widthMoved)
        {
            BeginAnimation(WidthProperty, null);
        }

        // 只有「高度要变」才值得走动画；宽度单独变化没有可动画的连续量，落印就地补上。
        if (!animate || !_settings.QuickWindowAdaptiveAnimation || double.IsNaN(fromWidth) || double.IsNaN(fromHeight)
            || !heightMoved)
        {
            BeginAnimation(WidthProperty, null);  // 清掉可能残留的动画，回到上面的基值
            BeginAnimation(HeightProperty, null);
            ReleaseInnerLayout();                 // 瞬时落地：上一轮静音的滚动条也要一并还原
            Reposition();
            FlushPendingSeal();                   // 尺寸已定：落印立即补上
            return;
        }

        var duration = TimeSpan.FromMilliseconds(AdaptiveAnimationMs);
        // 长高过程中译文框滚动条会「先出现、装下后消失」，每次显隐都改正文可用宽度、再折行一次。
        MuteResultScrollBarForAnimation();
        var topBefore = CurrentTopPhysical();
        var height = new DoubleAnimation(fromHeight, targetHeight, duration)
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        height.Completed += (_, _) =>
        {
            ReleaseInnerLayout(); // 收尾还原滚动条：此刻窗口已是终态高度，装不下才会出现滚动条
            // 这行是「锁住上边沿」的现场证据：两端的顶边必须一致（收尾摆放也只允许 ±1px 取整误差）。
            // 用户在真机上报「又跳了」时，直接拿它对照，不必靠肉眼猜。
            var topAfter = CurrentTopPhysical();
            Log.Information(
                "小窗尺寸动画 {Duration}ms：{FromW:F0}×{FromH:F0} → {ToW:F0}×{ToH:F0} DIP，顶边 {TopBefore} → {TopAfter} px（漂移 {Drift}）",
                AdaptiveAnimationMs, fromWidth, fromHeight, targetWidth, targetHeight,
                topBefore, topAfter,
                topBefore == int.MinValue || topAfter == int.MinValue ? "未知" : (topAfter - topBefore).ToString());
            ScheduleDeferredReposition();
            FlushPendingSeal(); // 尺寸与排版全部收尾后才落印（用户要求的顺序）
        };
        BeginAnimation(HeightProperty, height);
    }

    /// <summary>
    /// 生长动画期间静音译文框滚动条（14.2.4「不抖动」）：长高过程中 `VerticalScrollBarVisibility="Auto"`
    /// 会「先出现、装下后消失」，每次显隐都改正文可用宽度、触发一次重折行。
    /// Disabled = 不显示也不占位，正文可用宽度在整段动画里恒定；动画结束由
    /// <see cref="ReleaseInnerLayout"/> 按需恢复 Auto。宽度已改为瞬时到位，不再需要钉换行元素宽度。
    /// </summary>
    private void MuteResultScrollBarForAnimation()
    {
        ReleaseInnerLayout(); // 上一次动画还没收尾就又来一次：先按需恢复，再重新静音
        _frozenResultScrollBar = ResultBox.VerticalScrollBarVisibility;
        ResultBox.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    /// <summary>还原 <see cref="MuteResultScrollBarForAnimation"/> 静音的滚动条；重复调用安全。</summary>
    private void ReleaseInnerLayout()
    {
        if (_frozenResultScrollBar is { } scrollBar)
        {
            ResultBox.VerticalScrollBarVisibility = scrollBar;
            _frozenResultScrollBar = null;
        }
    }

    /// <summary>
    /// 把算好的目标尺寸再按「本次呼出还剩多少空间」收一次口：只收不放，且不低于默认尺寸
    /// （默认值是用户偏好，宁可让顶边让位——见 <see cref="ComputePhysicalRect"/>——也不把窗口压小）。
    /// 可用空间不可知（≤ 0）时原样返回。
    /// </summary>
    private static double CapByAvailableSpan(double resolvedDip, double baselineDip, double availableDip) =>
        availableDip <= 0 ? resolvedDip : Math.Min(resolvedDip, Math.Max(baselineDip, availableDip));

    /// <summary>设置的默认宽高（窗口 DIP，含阴影留白），已按取值范围夹取。</summary>
    private (double Width, double Height) DefaultWindowSizeDip() =>
        (WindowSizePolicy.ClampWidth(_settings.QuickWindowWidth),
         WindowSizePolicy.ClampHeight(_settings.QuickWindowHeight));

    /// <summary>窗口 DIP 尺寸 → 卡片意图尺寸（减去两侧阴影留白，且不小于 XAML 的最小尺寸）。</summary>
    private double ToIntentWidth(double windowWidthDip) =>
        Math.Max(SafeMin(MinWidth), windowWidthDip - ShadowMarginDip * 2);

    private double ToIntentHeight(double windowHeightDip) =>
        Math.Max(SafeMin(MinHeight), windowHeightDip - ShadowMarginDip * 2);

    private double ToWindowWidth(double intentWidthDip) => intentWidthDip + ShadowMarginDip * 2;

    private double ToWindowHeight(double intentHeightDip) => intentHeightDip + ShadowMarginDip * 2;

    /// <summary>
    /// 按实际布局量出「内容所需高度」（**窗口 DIP，含阴影留白**，与设置项同一坐标系）：
    /// 每一项都取真实元素的测量值/布局常量，算法里不写死数字（14.2.1 第 4 点）：
    /// 语言栏 + 译文标题行 + 进度/错误/状态行 + 输入区 + 译文区（影子测量）+ 窗口内边距 + 阴影留白。
    /// </summary>
    /// <param name="contentWidthDip">该宽度下的内容可用宽度（由 <see cref="ContentWidthFor"/> 算出）。</param>
    private double MeasureContentHeightDip(double contentWidthDip)
    {
        // 语言栏量的是包在外面的 40px 固定高 Border：只量里面的 Grid 会漏掉那圈高度（实测矮 16px）
        var chrome = MeasureElementDip(LanguageBar, contentWidthDip)
                     + MeasureElementDip(SeamRow, contentWidthDip)
                     + MeasureElementDip(StyleRow, contentWidthDip)
                     + MeasureElementDip(ReviewCard, contentWidthDip)
                     + MeasureElementDip(StatusPanel, contentWidthDip)
                     + SurfaceBorder.Padding.Top + SurfaceBorder.Padding.Bottom
                     + SurfaceBorder.Margin.Top + SurfaceBorder.Margin.Bottom;

        // 输入区：真实输入框自带 MinHeight 25 / MaxHeight 140 夹取，测它是安全的（不是只读结果区）
        var inputHeight = MeasureElementDip(InputBox, contentWidthDip);

        // 影子必须与真实译文区同一套排版参数（XAML 里 ResultBox 是 19px / 29.5 行高）
        MeasureShadow.FontSize = ResultFontSize;
        MeasureShadow.LineHeight = ResultLineHeight;

        // 译文区：影子测量（14.2.1 第 2 点明确禁止直接测只读结果 TextBox）
        // FR-043（P0 批 4）：对照视图打开时按「原文段 + 译文段」逐对测量
        if (_vm.IsAlignView && _vm.AlignPairs.Count > 0)
        {
            var alignHeight = 0.0;
            var smallFont = (double)FindResource("FontSize.Small");
            var contentFont = (double)FindResource("FontSize.Content");
            MeasureShadow.Padding = ResultBox.Padding;
            MeasureShadow.LineHeight = 21; // 与对照列表的行高一致
            foreach (var pair in _vm.AlignPairs)
            {
                MeasureShadow.FontSize = smallFont;
                MeasureShadow.Text = pair.Source;
                MeasureShadow.Measure(new Size(contentWidthDip, double.PositiveInfinity));
                alignHeight += MeasureShadow.DesiredSize.Height;

                MeasureShadow.FontSize = contentFont;
                MeasureShadow.Text = pair.Translated;
                MeasureShadow.Measure(new Size(contentWidthDip, double.PositiveInfinity));
                alignHeight += MeasureShadow.DesiredSize.Height + 12; // 段间距，与 XAML Margin 一致
            }

            MeasureShadow.FontSize = contentFont;
            MeasureShadow.FontSize = ResultFontSize;
            MeasureShadow.LineHeight = ResultLineHeight;
            return chrome + inputHeight + alignHeight + SubLineHeightDip(contentWidthDip)
                   + MeasureDictionaryCardHeightDip(contentWidthDip);
        }

        MeasureShadow.Text = _vm.ResultText ?? "";
        MeasureShadow.Padding = ResultBox.Padding; // 与真实结果区同一可用宽度（内边距一致）
        MeasureShadow.Measure(new Size(contentWidthDip, double.PositiveInfinity));
        // 空译文时影子只有一行高，但真实结果区有 MinHeight，取二者较大值才是实际占用
        var resultHeight = Math.Max(MeasureShadow.DesiredSize.Height, ResultArea.MinHeight);

        // FR-053：跟读模式每句独立成行且带行距，按句数补回这部分高度（否则会裁掉最后一句）
        if (_vm.IsShadowMode)
        {
            resultHeight += _vm.ShadowLines.Count * 8;
        }

        return chrome + inputHeight + resultHeight + SubLineHeightDip(contentWidthDip)
               + MeasureDictionaryCardHeightDip(contentWidthDip);
    }

    /// <summary>
    /// 译文副行（设计稿 .dst-sub）所需高度（DIP）：没有副行时 0。它贴着译文区底边排版，
    /// 不与正文抢高度，但必须算进窗口总高，否则它会把译文的最后一行压住。
    /// </summary>
    private double SubLineHeightDip(double availableWidth) =>
        ResultSubText.Visibility == Visibility.Visible
            ? MeasureElementDip(ResultSubText, availableWidth)
            : 0;

    /// <summary>
    /// 词典卡所需高度（DIP）：卡片隐藏时 0；可见时 = 卡内 StackPanel 的排版高度 + 内边距 + 外边距。
    /// 释义的 MaxHeight 由样式触发器按展开态夹好（40 / 180），这里测一次即可，不另算一遍。
    /// </summary>
    private double MeasureDictionaryCardHeightDip(double availableWidth)
    {
        if (DictionaryCard.Visibility != Visibility.Visible
            || DictionaryCard.Child is not FrameworkElement body)
        {
            return 0;
        }

        var inner = Math.Max(1, availableWidth
                              - DictionaryCard.Padding.Left - DictionaryCard.Padding.Right
                              - DictionaryCard.BorderThickness.Left - DictionaryCard.BorderThickness.Right);
        body.Measure(new Size(inner, double.PositiveInfinity));
        return body.DesiredSize.Height
               + DictionaryCard.Padding.Top + DictionaryCard.Padding.Bottom
               + DictionaryCard.Margin.Top + DictionaryCard.Margin.Bottom;
    }

    /// <summary>
    /// 按译文文本量出「内容所需宽度」（**窗口 DIP，含阴影留白**）：影子在**不限宽**下测量，
    /// 得到最长行的排版宽度——即"不折行放得下这些文字"所需的宽度。无译文/测量失败返回 0，
    /// 由 <see cref="WindowSizePolicy.ResolveWidth"/> 回落到默认宽度（只增不减）。
    /// </summary>
    private double MeasureNeededWidthDip()
    {
        var text = _vm.ResultText;
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        MeasureShadow.Text = text;
        MeasureShadow.Padding = ResultBox.Padding;
        MeasureShadow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = MeasureShadow.DesiredSize.Width;
        if (double.IsNaN(textWidth) || textWidth <= 0)
        {
            return 0;
        }

        return textWidth + SurfaceBorder.Padding.Left + SurfaceBorder.Padding.Right
                        + SurfaceBorder.Margin.Left + SurfaceBorder.Margin.Right;
    }

    /// <summary>给定窗口 DIP 宽度时内容的可用宽度（扣掉窗口内边距与阴影留白），与既有布局一致。</summary>
    private double ContentWidthFor(double windowWidthDip) =>
        Math.Max(
            windowWidthDip - ShadowMarginDip * 2 - SurfaceBorder.Padding.Left - SurfaceBorder.Padding.Right,
            120);

    /// <summary>测量一个真实元素的所需高度（含其 Margin——DesiredSize 不含 Margin，需补回）。</summary>
    private static double MeasureElementDip(FrameworkElement element, double availableWidth)
    {
        element.Measure(new Size(availableWidth, double.PositiveInfinity));
        return element.DesiredSize.Height + element.Margin.Top + element.Margin.Bottom;
    }

    /// <summary>
    /// 鼠标所在屏工作区高度（DIP，14.2.3 高度上限的基准）；取不到时返回 0，策略退化为只用 640 绝对上限。
    /// </summary>
    private double CurrentWorkAreaHeightDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !TryGetPlacementInput(hwnd, out _, out var work, out var dpi))
        {
            return 0;
        }

        var scale = dpi / 96.0;
        return scale > 0 ? (work.Bottom - work.Top) / scale : 0;
    }

    /// <summary>
    /// 锁住顶边后本次会话还能长到多高（**窗口 DIP**，含阴影留白）：顶边到工作区下沿的距离。
    /// 顶边尚未落地（呼出首帧）时退化为整个工作区高度——首次摆放会重新判方向与锚点。
    /// 它是「顶边不跳」的前提：目标高度永远落在可用空间内，<see cref="WindowPlacement.PinTopEdge"/> 就永远不用让位。
    /// </summary>
    private double AvailableWindowHeightDip()
    {
        var (work, scale) = WorkContext();
        return scale > 0 ? WindowPlacement.AvailableHeightBelow(_pinnedTopPhysical, work) / scale : 0;
    }

    /// <summary>
    /// 光标所在贴边方向上还能容纳多宽（**窗口 DIP**，含阴影留白）：加宽到越界、再被向左钳制，
    /// 就是横向的「拉宽跳变」，所以限宽与限高同理。方向未定（首次摆放前）按整个工作区宽。
    /// </summary>
    private double AvailableWindowWidthDip()
    {
        var (work, scale) = WorkContext();
        if (scale <= 0)
        {
            return 0;
        }

        if (_placementSide.IsAuto || _placementAnchor is not { } anchor)
        {
            return work.Width / scale;
        }

        var gap = WindowPlacement.ToPhysicalLength(WindowPlacement.CursorGapDip, scale);
        return WindowPlacement.AvailableWidthOnSide(_placementSide, anchor.X, work, gap) / scale;
    }

    /// <summary>鼠标所在屏工作区（物理像素）与目标缩放；取不到时 scale 返回 0，调用方退让。</summary>
    private (PhysicalRect Work, double Scale) WorkContext()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !TryGetPlacementInput(hwnd, out _, out var work, out var dpi))
        {
            return (default, 0);
        }

        return (new PhysicalRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), dpi / 96.0);
    }

    /// <summary>
    /// 窗口当前**物理顶边**（无 HWND 或取不到时返回 <see cref="int.MinValue"/>，只影响日志显示）。
    /// 用作尺寸动画前后「顶边有没有动」的现场证据。
    /// </summary>
    private int CurrentTopPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && ScreenInterop.GetWindowRect(hwnd, out var actual)
            ? actual.Top
            : int.MinValue;
    }

    /// <summary>
    /// 把意图 DIP 尺寸同步到 WPF 属性：<c>Width/Height</c> 是**含阴影留白的窗口 DIP 尺寸**
    /// （= 意图尺寸 + 2×<see cref="ShadowMarginDip"/>），与设置项、物理摆放同一坐标系
    /// （14.1.2 第 4 步：物理尺寸 = round((意图 + 2 × 阴影留白) × scale)）。
    /// WPF 会在摆放后按物理尺寸回写属性，但意图始终以上面的字段为准。
    /// </summary>
    private void SyncSizePropertiesFromIntent()
    {
        Width = ToWindowWidth(_intentWidthDip);
        Height = ToWindowHeight(_intentHeightDip);
    }

    private static double SafeMin(double min) => double.IsNaN(min) ? 0 : min;

    private void OnForegroundEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND
            || idObject != NativeMethods.OBJID_WINDOW
            || hwnd == new WindowInteropHelper(this).Handle)
        {
            return;
        }

        if (!IsVisible || _vm.IsPinned)
        {
            return;
        }

        Log.Debug("前台变化（新前台={Hwnd}）→ 安排隐藏", hwnd);
        Dispatcher.BeginInvoke(ScheduleHide);
    }

    private void ScheduleHide()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        // 期间窗口内重新获得键盘焦点（点击小窗内部）则取消隐藏
        if (IsVisible && !_vm.IsPinned && !IsKeyboardFocusWithin)
        {
            HideWindow();
        }
    }

    private void HideWindow()
    {
        Log.Debug("隐藏小窗（键盘焦点在本窗口内={FocusWithin}）", IsKeyboardFocusWithin);
        _hideTimer.Stop();
        _adaptiveTimer.Stop();
        ReleaseInnerLayout(); // 动画没跑完就收起小窗时，别把静音的滚动条带进下一次呼出
        _adaptivePendingMs = 0;
        _pendingSeal = false; // 本会话作废：收起时还没播的落印不再补播
        UnhookForeground();

        // FR-020 AC 4：关窗即取消在途对比请求，不留后台任务
        _vm.CancelComparison();

        // 单栏请求同样随窗口隐藏失效：迟到响应不得再写回下一次会话。
        _vm.CancelTranslation();

        // FR-053：跟读同理——窗口都隐藏了，语音不能还在念下一句
        _vm.CancelShadowReading();

        // FR-026：**隐藏时不写回任何尺寸**——缩放手势只影响本次窗口，
        // 下次呼出仍从设置里的默认宽高与当前内容重新计算（对比模式的临时增高也因此不会被持久化）。
        Hide();
        RestorePreviousForeground();
    }

    /// <summary>对比模式相关状态变化与 FR-026 的自适应触发时机（14.2.4）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QuickTranslateViewModel.IsComparing):
                if (_vm.IsComparing)
                {
                    ExpandForComparison();
                }
                else
                {
                    RestoreAfterComparison();
                }
                return;

            // 14.2.4 时机 2/3/5：输入文本、译文返回、进度条/错误行/状态行显隐都会改变所需高度。
            // 走防抖而非立即重算：IsBusy 与 ResultText 是相继变化的，
            // 立即测量会把「翻译中」的进度条高度也算进窗口（本会话只增不减，多出来的高度再也去不掉）。
            // D-④：译文返回单独用 120ms 短防抖（译文一到就该长，别让用户对着滚动条等 400ms）；
            // 其余来源维持 400ms。
            case nameof(QuickTranslateViewModel.InputText):
            case nameof(QuickTranslateViewModel.IsBusy):
            case nameof(QuickTranslateViewModel.StatusText):
            case nameof(QuickTranslateViewModel.ErrorText):
            // FR-049：词典卡出现/消失、展开/收起都改变所需高度（同样走防抖，避免连续两次重排）
            case nameof(QuickTranslateViewModel.DictionaryDefinition):
            case nameof(QuickTranslateViewModel.IsDictionaryExpanded):
            // FR-051：换说法行只在 AI 引擎下出现，出现即多占一行
            case nameof(QuickTranslateViewModel.SupportsStyle):
            // FR-052：复习行出现/消失同样占一行
            case nameof(QuickTranslateViewModel.ReviewText):
                ScheduleAdaptiveRecompute();
                return;

            // FR-053：跟读模式切换改变结果区排版（整块 ⇄ 逐句），按译文返回同档短防抖重算
            case nameof(QuickTranslateViewModel.IsShadowMode):
                ScheduleAdaptiveRecompute(ResultDebounceMs);
                return;

            case nameof(QuickTranslateViewModel.ResultText):
                ScheduleAdaptiveRecompute(ResultDebounceMs);
                // 用户要求：弹窗尺寸与文字排版全部执行完再落印。这里只登记，等尺寸出口（动画收尾 / 无需变化）消费。
                _pendingSeal = true;
                return;

            // FR-043（P0 批 4）：对照视图开合改变内容高度，按译文返回同档短防抖重算
            case nameof(QuickTranslateViewModel.IsAlignView):
                ScheduleAdaptiveRecompute(ResultDebounceMs);
                return;
        }
    }

    /// <summary>点词典卡任意处 = 展开 / 收起释义（FR-049：默认两行，长文不必占满小窗）。</summary>
    private void OnDictionaryCardClick(object sender, MouseButtonEventArgs e)
    {
        _vm.ToggleDictionaryExpandedCommand.Execute(null);
        e.Handled = true;
    }


    /// <summary>
    /// 落印（全界面唯一的签名动效，设计稿 @keyframes land / inkmist 的**力量强化版**）：
    /// 译文落定那刻，右上角那枚印面标从**高处**沿透视砸下：起点 Y −78px / 放大 2.0 倍 / 纵向压成 1.42——
    /// 「高处的印离眼睛更近，所以更大；越靠近纸面越小、越正」，这就是透视落印的读数依据；再配上它脚下的
    /// 落点影（SealShadow）由小（0.35 倍、淡 0.08）到大（1.35 倍、实 0.36）——影小→大 = 离纸面由远及近，
    /// 单靠缩放看不出「高」，有这层影高度才成立。
    /// 落纸瞬间（82ms）同时打出这些重音（全部静止态不可见、不占排版）：
    /// ① 卡片震荡（SurfaceShake）：Y 7.5 / −4.0 / +2.4 / −1.2 / +0.5 四次衰减来回，X −5.5 / +3.6 / −2.0 / +1.0 / −0.4
    ///    错相横抖——不是一次回弹，而是余震；
    /// ② 震痕 3 道（SealImpactLine/2/3）：横贯译文的 2px 骑缝细线自印面标向左展开，逐条晚 46ms、向下错开 6px，
    ///    像纸面余波一圈圈往下荡开；横向铺满卡片，永远不会被卡片右缘裁掉；
    /// ③ 6 粒飞溅墨点（画布 200×200，最远飞 86px，逐粒错开 16ms，460ms 淡尽）+ 墨气（扩到 3.4 倍）；
    /// ④ 冲击闪光（200ms，峰值 1.0）。
    /// 印面标自身落纸后也不止一次回弹：Y / 缩放 / 旋转各做 4 个衰减来回，落纸挤扁 X1.24 / Y0.74、手歪从 −12° 起。
    /// 下压段用 ease-in（越接近纸面越快，像砸下去，不是飘下来）。
    /// 注：旧版把墨点/闪光/墨气关在 22×22 的格子里（墨点最远 22px、闪光最大 53px），所以「加固」在真机上感知不到——
    /// 力量感的第一来源是**幅度**，其次才是曲线。设计稿原是一条 ease-stamp 缓到底，位移与缩放同曲线，没有重量。
    /// 落下之后印面标常驻实心朱砂（纸面上一枚红印，与设计稿渲染出的终态一致）。
    /// 只在单结果成功落定时播放——没盖上不给奖赏（印面转成虚线空心印框）；
    /// 系统关闭「在窗口内显示动画」（ClientAreaAnimation，reduced-motion 的 Windows 等效）时整段跳过。
    /// 预算是「一瞬间的事」：总时长 260ms 不变，加重靠曲线、挤扁与反震，不靠拉长。
    /// </summary>
    private void PlaySealStamp()
    {
        if (!BuildSealStamp())
        {
            return;
        }

        Log.Information("落印：译文落定，压印 {Stamp}ms（透视砸落 −78px；落纸 {Impact}ms 起卡片震荡 + 震痕 3 道 + 墨爆 6 粒 + 墨气 {Mist}ms）",
            StampMs, ImpactMs, InkMistDuration.TotalMilliseconds);
    }
    /// <summary>
    /// 构建并直接播放落印的完整动画；返回 <c>false</c> 表示这次不该落印：
    /// 控件缺失 / 系统关了窗口内动画 / 没有译文 / 出错 / 对比模式。
    /// 动画一律直接挂到 Transform / 元素本体上（见 <see cref="Attach"/>），不经 Storyboard。
    /// </summary>
    private bool BuildSealStamp()
    {
        if (SealMark is null || SealMarkScale is null || SealMarkShift is null
            || SealMarkSpin is null || SealFlash is null || SealFlashScale is null
            || SealHalo is null || SealHaloScale is null || SurfaceShake is null
            || SealImpactLine is null || SealImpactLineScale is null
            || SealImpactLine2 is null || SealImpactLine2Scale is null
            || SealImpactLine3 is null || SealImpactLine3Scale is null
            || SealShadow is null || SealShadowScale is null
            || SealDropShift1 is null || SealDropShift2 is null || SealDropShift3 is null
            || SealDropShift4 is null || SealDropShift5 is null || SealDropShift6 is null)
        {
            return false;
        }

        if (!SystemParameters.ClientAreaAnimation) return false;
        if (string.IsNullOrEmpty(_vm.ResultText) || _vm.HasError || _vm.IsComparing) return false;

        static KeyTime At(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));

        // 分段曲线：下压段 ease-in（越接近纸面越快，像砸下去）、回弹快出慢入、归位稳定收敛。
        // 设计稿只用了一条 ease-stamp；力量感恰恰来自这三段曲线的对比。
        var press = new KeySpline(0.55, 0, 1, 1);
        var snap = new KeySpline(0.2, 0.9, 0.3, 1);
        var settle = new KeySpline(0.25, 0, 0.4, 1);
        var impact = TimeSpan.FromMilliseconds(ImpactMs);

        // 印面标：从高处砸下（透视）。起点 Y −78px / 放大 2.0 倍 / 纵向压扜到 1.42——
        // 「高处的印离眼睛更近，所以更大；越靠近纸面越小、越正」——这就是透视落印的读数依据。
        // 落纸后不再是单次回弹，而是 4 个逐次衰减的来回（震荡感）；位移与挤扁分开驱动，才是「硬压」。
        var drop = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(-78, At(0)));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(-38, At(55), press));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(3, At(ImpactMs), press));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(-3.4, At(118), snap));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(2.0, At(152), snap));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(-1.1, At(190), settle));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(0.5, At(226), settle));
        drop.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(StampMs), settle));

        var stretchX = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(2.00, At(0)));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(1.62, At(55), press));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(1.24, At(ImpactMs), press));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(0.93, At(118), snap));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(1.06, At(152), snap));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(0.98, At(190), settle));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(1.008, At(226), settle));
        stretchX.KeyFrames.Add(new SplineDoubleKeyFrame(1, At(StampMs), settle));

        var stretchY = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(1.42, At(0)));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(1.16, At(55), press));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(0.74, At(ImpactMs), press));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(1.11, At(118), snap));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(0.95, At(152), snap));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(1.03, At(190), settle));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(0.994, At(226), settle));
        stretchY.KeyFrames.Add(new SplineDoubleKeyFrame(1, At(StampMs), settle));

        // 手歪：从高处歪 12° 落下，落纸后左右补救地旋两个来回，落定后归 0（静止态不留倾斜）。
        var spin = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(-12, At(0)));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(-8, At(55), press));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(-3, At(ImpactMs), press));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(1.5, At(118), snap));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(-0.8, At(152), snap));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(0.4, At(190), settle));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(226), settle));
        spin.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(StampMs), settle));

        // 印面标在悬印段半透（看得见印在头顶）→ 落纸那一瞬爆到实心；之后**保持**实心朱砂（HoldEnd）。
        // 用 Stop 的话动画一完就回到 XAML 里的静止态 0.13 —— 印面标会当场变淡，看上去像“没落印”。
        var appear = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.HoldEnd };
        appear.KeyFrames.Add(new LinearDoubleKeyFrame(0, At(0)));
        appear.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, At(55)));
        appear.KeyFrames.Add(new LinearDoubleKeyFrame(1, At(ImpactMs)));
        appear.KeyFrames.Add(new LinearDoubleKeyFrame(1, At(StampMs)));

        // 冲击闪光：落纸瞬间在印面标下方炸开一团实朱砂，200ms 内扩到 2.1 倍并淡尽。
        var flashMs = TimeSpan.FromMilliseconds(200);
        var flashEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var flashScale = new DoubleAnimation(0.4, 2.4, flashMs)
        {
            BeginTime = impact, EasingFunction = flashEase, FillBehavior = FillBehavior.Stop,
        };
        var flashFade = new DoubleAnimation(1.0, 0, flashMs)
        {
            BeginTime = impact, EasingFunction = flashEase, FillBehavior = FillBehavior.Stop,
        };

        // 墨气：从 0.45 倍扩到 2.6 倍，同时淡到看不见（460ms，比闪光慢，收尾留一层余韵）
        var mistEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var mistScale = new DoubleAnimation(0.45, 3.4, InkMistDuration)
        {
            BeginTime = impact, EasingFunction = mistEase, FillBehavior = FillBehavior.Stop,
        };
        var mistFade = new DoubleAnimation(0.45, 0, InkMistDuration)
        {
            BeginTime = impact, EasingFunction = mistEase, FillBehavior = FillBehavior.Stop,
        };

        // 落点影（透视落印的「高度锚」）：印在高处时影小而淡，越接近纸面越大越实，砸到纸面那刻达到最大，
        // 随后淡尽。影子由小到大 = 离纸面由远及近——这是让「高」被看见的关键，单靠缩放不够。
        var shadowScale = new DoubleAnimation(0.35, 1.35, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop,
        };
        var shadowFade = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(430), FillBehavior = FillBehavior.Stop,
        };
        shadowFade.KeyFrames.Add(new LinearDoubleKeyFrame(0.08, At(0)));
        shadowFade.KeyFrames.Add(new LinearDoubleKeyFrame(0.16, At(55)));
        shadowFade.KeyFrames.Add(new SplineDoubleKeyFrame(0.36, At(ImpactMs), press));
        shadowFade.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(ImpactMs + 330), settle));

        // 落纸反震：落纸瞬间整张卡片被砸得向下一顿，横向再抖两下，然后收敛回原位。
        // 这是「力量感」的重音——只有印面标在动、卡片不动时，看上去只是一枚标记飘下来，没有重量。
        // 同样以 impact 为起点，结尾归 0（不参与排版、不改设计稿静态观感）。
        var recoilY = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(0)));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(ImpactMs), press));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(7.5, At(ImpactMs + 22), snap));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(-4.0, At(ImpactMs + 54), snap));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(2.4, At(ImpactMs + 86), snap));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(-1.2, At(ImpactMs + 120), settle));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(0.5, At(ImpactMs + 156), settle));
        recoilY.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(StampMs), settle));

        var recoilX = new DoubleAnimationUsingKeyFrames { Duration = StampDuration, FillBehavior = FillBehavior.Stop };
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(0)));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(ImpactMs), press));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(-5.5, At(ImpactMs + 18), snap));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(3.6, At(ImpactMs + 48), snap));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(-2.0, At(ImpactMs + 80), snap));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, At(ImpactMs + 114), settle));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(-0.4, At(ImpactMs + 148), settle));
        recoilX.KeyFrames.Add(new SplineDoubleKeyFrame(0, At(StampMs), settle));

        // 震痕 / 余震：三条横贯译文的骑缝细线自印面标向左展开并淡去，各向下错开 6px、逐条晚 46ms 起播——
        // 像纸面被砸出的余波一圈圈往下荡开。横向铺满整张卡片，所以永远不会被卡片右缘裁掉。
        static void AttachRipple(ScaleTransform lineScale, UIElement line, double beginMs, double peak)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var ms = TimeSpan.FromMilliseconds(300);
            var begin = TimeSpan.FromMilliseconds(beginMs);
            Attach(new DoubleAnimation(0.02, 1, ms)
            {
                BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop,
            }, lineScale, ScaleTransform.ScaleXProperty);
            Attach(new DoubleAnimation(peak, 0, ms)
            {
                BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop,
            }, line, UIElement.OpacityProperty);
        }

        // 墨爆：6 粒墨点各按自己的角度飞出去（最远 86px），逐粒错开 16ms，460ms 内淡尽（错位形成方向感）
        var drops = new[] { SealDropShift1, SealDropShift2, SealDropShift3, SealDropShift4, SealDropShift5, SealDropShift6 };
        var dots = new[] { SealDrop1, SealDrop2, SealDrop3, SealDrop4, SealDrop5, SealDrop6 };
        var burst = TimeSpan.FromMilliseconds(460);
        var flyEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 清掉上一轮可能残留的动画（连按两次落印时，这一印必须从头顶重来）
        SealMark.BeginAnimation(OpacityProperty, null);
        SealMarkScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealMarkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SealMarkShift.BeginAnimation(TranslateTransform.YProperty, null);
        SealMarkSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        SealFlash.BeginAnimation(OpacityProperty, null);
        SealFlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealFlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SealHalo.BeginAnimation(OpacityProperty, null);
        SealHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SurfaceShake.BeginAnimation(TranslateTransform.XProperty, null);
        SurfaceShake.BeginAnimation(TranslateTransform.YProperty, null);
        SealImpactLine.BeginAnimation(OpacityProperty, null);
        SealImpactLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealImpactLine2.BeginAnimation(OpacityProperty, null);
        SealImpactLine2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealImpactLine3.BeginAnimation(OpacityProperty, null);
        SealImpactLine3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealShadow.BeginAnimation(OpacityProperty, null);
        SealShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SealShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        foreach (var shift in drops)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
        }

        foreach (var dot in dots)
        {
            dot.BeginAnimation(OpacityProperty, null);
        }

        Attach(drop, SealMarkShift, TranslateTransform.YProperty);
        Attach(stretchX, SealMarkScale, ScaleTransform.ScaleXProperty);
        Attach(stretchY, SealMarkScale, ScaleTransform.ScaleYProperty);
        Attach(spin, SealMarkSpin, RotateTransform.AngleProperty);
        Attach(appear, SealMark, UIElement.OpacityProperty);
        Attach(flashScale, SealFlashScale, ScaleTransform.ScaleXProperty);
        Attach(flashScale, SealFlashScale, ScaleTransform.ScaleYProperty);
        Attach(flashFade, SealFlash, UIElement.OpacityProperty);
        Attach(mistScale, SealHaloScale, ScaleTransform.ScaleXProperty);
        Attach(mistScale, SealHaloScale, ScaleTransform.ScaleYProperty);
        Attach(mistFade, SealHalo, UIElement.OpacityProperty);
        Attach(recoilX, SurfaceShake, TranslateTransform.XProperty);
        Attach(recoilY, SurfaceShake, TranslateTransform.YProperty);
        AttachRipple(SealImpactLineScale, SealImpactLine, ImpactMs, 0.72);
        AttachRipple(SealImpactLine2Scale, SealImpactLine2, ImpactMs + 46, 0.42);
        AttachRipple(SealImpactLine3Scale, SealImpactLine3, ImpactMs + 96, 0.24);
        Attach(shadowScale, SealShadowScale, ScaleTransform.ScaleXProperty);
        Attach(shadowScale, SealShadowScale, ScaleTransform.ScaleYProperty);
        Attach(shadowFade, SealShadow, UIElement.OpacityProperty);

        for (var i = 0; i < drops.Length; i++)
        {
            var (angleDeg, distance) = SealDrops[i];
            var radians = angleDeg * Math.PI / 180;
            var delay = impact + TimeSpan.FromMilliseconds(i * 16);
            Attach(new DoubleAnimation(0, Math.Cos(radians) * distance, burst)
            {
                BeginTime = delay, EasingFunction = flyEase, FillBehavior = FillBehavior.Stop,
            }, drops[i], TranslateTransform.XProperty);
            Attach(new DoubleAnimation(0, Math.Sin(radians) * distance, burst)
            {
                BeginTime = delay, EasingFunction = flyEase, FillBehavior = FillBehavior.Stop,
            }, drops[i], TranslateTransform.YProperty);
            Attach(new DoubleAnimation(1, 0, burst)
            {
                BeginTime = delay, EasingFunction = flyEase, FillBehavior = FillBehavior.Stop,
            }, dots[i], UIElement.OpacityProperty);
        }

        return true;
    }

    /// <summary>
    /// 消费 <see cref="_pendingSeal"/> 并播放落印：**只在尺寸与排版全部收尾之后**调用
    /// （用户要求「弹窗大小和文字排版全部执行完后再落印」）。
    /// 用 <see cref="DispatcherPriority.Loaded"/> 排一帧：此时动画收尾的滚动条恢复与收尾摆放都已进入渲染树，
    /// 印面标压下去时译文已是最终排版，不会再被重排冲掉。
    /// </summary>
    private void FlushPendingSeal()
    {
        if (!_pendingSeal)
        {
            return;
        }

        _pendingSeal = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PlaySealStamp));
    }

    /// <summary>
    /// 把一段动画直接挂到某个可动对象（Transform / 元素）的依赖属性上。
    /// **不走 Storyboard.SetTarget**：实测那样指向 Freezable（TranslateTransform / ScaleTransform /
    /// RotateTransform）时是**静默失效**的——时钟照建、日志照打，属性值却一动不动，
    /// 界面上只能看到直接挂在元素上的不透明度在变。
    /// （2026-10-03 由 --seal-probe 仪表读回实测确认：Opacity 在动，所有 Transform 属性恒为初值。）
    /// BeginTime 由动画自己携带，所以错峰起播不需要 Storyboard。
    /// 同一段曲线要同时驱动 ScaleX/ScaleY 时必须各克隆一份（一个动画实例只能被一个时钟驱动）。
    /// </summary>
    private static void Attach(AnimationTimeline animation, IAnimatable owner, DependencyProperty property)
    {
        owner.BeginAnimation(property, (AnimationTimeline)animation.Clone());
    }

    /// <summary>14.2.4 时机 3/5：输入变化与错误行变化共用 400ms 防抖（译文返回用 120ms，自适应关闭时无需测量）。
    /// 同一批属性变更里按「更早的到期时间赢」合并（<see cref="WindowSizePolicy.MergeDebounceMs"/>）。</summary>
    private void ScheduleAdaptiveRecompute(int debounceMs = AdaptiveDebounceMs)
    {
        if (WindowSizePolicy.IsManual(_settings.QuickWindowSizeMode))
        {
            // manual 模式下自适应整体停用：留一条 Debug 日志，便于以后诊断「为什么窗口没有按内容变大」
            Log.Debug("小窗自适应已关闭（manual），忽略尺寸重算（防抖 {Debounce} ms）", debounceMs);
            FlushPendingSeal(); // 自适应关闭：尺寸不变，落印不必等
            return;
        }

        _adaptiveTimer.Stop();
        // 同一批属性变更里「更早的到期时间赢」：翻译成功收尾是 ResultText(120ms) → StatusText / IsBusy(400ms)，
        // 照单全收就会把译文到达的生长一路推到 400ms（用户看到的是「译文先出现、窗口停一下再长」）。
        var effectiveMs = WindowSizePolicy.MergeDebounceMs(_adaptivePendingMs, debounceMs);
        _adaptiveTimer.Interval = TimeSpan.FromMilliseconds(effectiveMs);
        _adaptivePendingMs = effectiveMs;
        _adaptiveTimer.Start();
    }

    /// <summary>13.4.2：2 栏至少 400，3 栏及以上至少 460；仅在高度不足时增高，增高后必须重新定位（FR-025）。</summary>
    private void ExpandForComparison()
    {
        var required = _vm.ComparisonColumnCount > 2 ? ComparisonHeightManyColumns : ComparisonHeightTwoColumns;
        _heightBeforeCompare ??= _intentHeightDip;

        var requiredCard = required - ShadowMarginDip * 2;
        if (_intentHeightDip < requiredCard)
        {
            _intentHeightDip = requiredCard;
            _adaptiveGuardUntil = Environment.TickCount64 + AdaptiveGuardMs;
            // 对比增高是即时落地：先撤掉可能还在跑的自适应宽高动画，否则两边同时写尺寸会打架
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            SyncSizePropertiesFromIntent();
            ReleaseInnerLayout(); // 自适应动画被即时落地顶掉：静音的滚动条一并还原
        }

        Log.Debug("进入对比模式：{Columns} 栏，窗口高度 {Height}（进入前 {Original}）",
            _vm.ComparisonColumnCount, _intentHeightDip, _heightBeforeCompare);

        // 连带修复（FR-025）：临时增高后必须重新摆放，否则窗口在屏幕下边缘会溢出工作区
        Reposition();
    }

    /// <summary>
    /// FR-026（14.2.4 协调规则）：退出对比模式时**不恢复快照，而是按默认值与当前译文重新算**——
    /// 快照本身就是上一次的计算值，恢复它等于忽略对比期间新增的译文。不写回设置。
    /// </summary>
    private void RestoreAfterComparison()
    {
        _heightBeforeCompare = null;
        if (!IsVisible)
        {
            return; // 呼出时的状态重置会走到这里：隐藏状态下无需重算，交给「呼出」那一次
        }

        Log.Debug("退出对比模式：按默认值/内容重新计算尺寸");

        // 立即落地（不动画）后再摆放，避免动画与随后的物理摆放争尺寸
        ApplyAdaptiveSize("退出对比模式", animate: false);
        Reposition();
    }

    /// <summary>重新激活时若「结果对比」的勾选集合已变更，清空过期结果（13.4.1）。</summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        try
        {
            if (_vm.NotifyCompareSelectionChanged())
            {
                Log.Information("对比引擎集合已变更，已清空对比结果");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "对比引擎集合变更检测失败");
        }
    }

    /// <summary>
    /// 把焦点还给呼出前的应用（仅在隐藏是由 Esc/关闭按钮/热键切换触发时）。
    /// 若用户已点击其它应用，则前台已不是本窗口，不做任何抢占。
    /// </summary>
    private void RestorePreviousForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            return;
        }

        var target = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return;
        }

        try
        {
            NativeMethods.SetForegroundWindow(target);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "还原原前台窗口失败");
        }
    }

    private void HookForeground()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            return;
        }
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void UnhookForeground()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideWindow(); // FR-003：Esc 快速关闭
            e.Handled = true;
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // FR-040（批 3）粘贴即译：输入框为空时 Ctrl+V 不粘贴，把剪贴板文本交给 App 走完整链路
        // （清洗→翻译）；输入框已有内容时保持普通粘贴——用户可能在续写/编辑。
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && _settings.PasteTranslateEnabled && InputBox.Text.Length == 0)
        {
            e.Handled = true;
            string? clipboardText = null;
            try
            {
                if (Clipboard.ContainsText())
                {
                    clipboardText = Clipboard.GetText();
                }
            }
            catch
            {
                // 剪贴板被占用：按空处理，绝不崩
            }
            PasteTranslateRequested?.Invoke(this, clipboardText);
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        // Shift+Enter = 换行：多段原文才可能触发「对照」逐段视图（FR-043），
        // 输入框此前 AcceptsReturn=False 时那段文字根本进不来，对照永远不可达。
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _vm.SwapCommand.Execute(null); // Ctrl+Enter 交换语言（FR-004）
        }
        else
        {
            _vm.TranslateCommand.Execute(null); // 回车翻译
        }
        e.Handled = true;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _vm.IsPinned = !_vm.IsPinned;
        if (_vm.IsPinned)
        {
            _hideTimer.Stop();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideWindow();

    /// <summary>
    /// 无边框窗口的拖动：在窗体表面空白处按下即可拖动。
    /// 语言栏控件、按钮、输入框、译文区自己在处理鼠标事件，此处只响应落到表面上的点击。
    /// </summary>
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (IsInteractiveElement(source))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已释放，忽略
        }
    }

    /// <summary>判断命中的元素是否属于可交互控件（是则不触发拖动）。</summary>
    private static bool IsInteractiveElement(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox or ScrollBar)
            {
                return true;
            }

            if (current is Window)
            {
                break;
            }
        }

        return false;
    }
}
