using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TranslationApp.Core.Anki;
using TranslationApp.Core.Capture;
using TranslationApp.Core.History;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Services;
using TranslationApp.Theming;
using TranslationApp.ViewModels;
using TranslationApp.Windows;

namespace TranslationApp;

/// <summary>
/// 应用入口：Serilog 初始化 → 全局异常处理 → 单实例检查 → DI 组装 →
/// 托盘初始化 → 全局热键注册（FR-001/008）。
/// 启动不显示主窗口，仅驻留系统托盘并弹气泡提示。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private HotkeyManager? _hotkeyManager;
    private HoverTriggerLogic? _hoverLogic;
    private ModifierKeyDoubleTapDetector? _doubleTap;
    private bool _verboseStartup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InitLogging(e.Args.Contains("--verbose"));
        _verboseStartup = e.Args.Contains("--verbose");
        RegisterGlobalExceptionHandlers();

        // 单实例：重复启动时不弹对话框（会残留进程并挡住前台），改为请已有实例显形后本实例立即退出
        _singleInstance = new SingleInstanceGuard("Local\\TranslationApp.SingleInstance");
        if (!_singleInstance.IsFirstInstance)
        {
            Log.Information("检测到已有实例在运行（新实例 PID {Pid}），已请求其显形，本实例退出", Environment.ProcessId);
            Shutdown(0);
            return;
        }

        // 已有实例收到再次启动请求时，打开设置窗口显形
        _singleInstance.ActivationRequested += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ShowSettings));

        BuildServices();

        // 主题需在窗口创建前应用，避免首帧闪烁（FR-019）
        var settings = _services!.GetRequiredService<AppSettings>();
        ThemeManager.Apply(SettingsViewModel.ToAppTheme(settings.Theme));

        try
        {
            InitTray();
            ShowStartBalloonIfEnabled();
            if (_verboseStartup && settings.PrivacyMode)
            {
                // spec §2.2：用户明确要了 --verbose 但隐私模式压住了日志，必须说明而不是静默失效
                _trayIcon!.ShowNotification("速译", "隐私模式开启中，--verbose 日志不落盘", NotificationIcon.Info);
            }
            RegisterHotkeysFromSettings();
            InitClipboardMonitor(settings);
            InitHoverSelect();
            InitDoubleTap();
            InitPasteTranslate();
            InitLocalApi();
            InitScreenCapture();
            InitEngineFallback();
            WarmUpEngine();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "托盘/热键初始化失败，应用退出");
            Shutdown(1);
            return;
        }

        // 命令行参数（FR-012）：--settings 直接打开设置窗口；--minimized 为默认行为
        if (e.Args.Contains("--settings"))
        {
            ShowSettings();
        }

        Log.Information("速译启动完成（托盘常驻，PID {Pid}）", Environment.ProcessId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // FR-001/008：退出时注销热键、释放托盘图标、监听器与互斥体
        _hotkeyManager?.Dispose();
        _services?.GetService<PinWindowManager>()?.CloseAll(); // FR-027：退出前释放钉图位图
        _services?.GetService<ClipboardMonitor>()?.Dispose();
        _services?.GetService<MouseButtonHook>()?.Dispose(); // FR-036/039：卸载鼠标钩子
        _services?.GetService<KeyboardButtonHook>()?.Dispose(); // FR-038：卸载键盘钩子
        _services?.GetService<TranslationApp.Services.LocalApiGateway>()?.Stop(); // FR-046：关监听
        _services?.GetService<ITtsService>()?.Stop();
        _trayIcon?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        Log.Information("速译已退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>初始化滚动文件日志；--verbose 时输出 Debug 级（排障用）。隐私模式下完全不落盘（P0 批 1 / spec §2.2）。</summary>
    private static void InitLogging(bool verbose)
    {
        if (PrivacyPeek())
        {
            Log.Logger = new LoggerConfiguration().CreateLogger(); // 无 sink：启动即不写任何日志文件
            return;
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TranslationApp", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, // 7 天滚动
                shared: true,              // 允许重复启动的短命实例并发写同一文件
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("=== 速译启动（PID {Pid}）===", Environment.ProcessId);
    }

    /// <summary>日志初始化早于 DI，隐私模式只能先直读配置文件探一次（读失败按关闭处理）。</summary>
    private static bool PrivacyPeek()
    {
        try
        {
            return new JsonSettingsStore().Load().PrivacyMode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 全局异常兜底：只记日志、不崩溃（需求 6 可靠性：引擎失败/断网不崩溃）。
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI 线程未处理异常（已拦截，程序继续运行）");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常（已标记 Observed）");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new Exception(args.ExceptionObject?.ToString() ?? "未知异常对象");
            Log.Fatal(ex, "AppDomain 未处理异常（IsTerminating={IsTerminating}）", args.IsTerminating);
        };
    }

    private void BuildServices()
    {
        var store = new JsonSettingsStore();
        var settings = store.Load();

        // 历史/生词本数据库（FR-014/015）：初始化失败只降级该功能，不影响翻译主流程
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TranslationApp", "history.db");
        var database = new HistoryDatabase(databasePath);
        database.Initialize();
        if (!database.IsAvailable)
        {
            Log.Warning("历史数据库不可用（{Reason}），历史与生词本功能已降级关闭", database.UnavailableReason);
        }

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore>(store);
        services.AddSingleton(settings);

        // Bing 页面令牌依赖 Cookie 会话；需使用真实浏览器 UA，否则被风控拒绝（实测极简 UA 会返回 401）。
        // 客户端统一由 HttpClientProvider 提供，以便代理设置（FR-018）在运行时对两个引擎即时生效。
        var httpProvider = new HttpClientProvider
        {
            ProxyResolver = ResolveProxy,
        };
        services.AddSingleton(httpProvider);
        // P0 批 1 / spec §1.3：引擎先注册为具体类型，再由 TranslatorCatalog 工厂统一包 GlossaryTranslator
        // （术语替换 + 成败统计），小窗/钉图/对比/测试连接等全部调用点零改动生效。
        services.AddSingleton<BingTranslator>();
        // FR-028：显式传入设置，让 Google 在开启自动降级时把网络重试降为 0
        // （最坏等待从 2×8s 收到 8s，8s 内即轮到 Bing）——写成工厂，不依赖容器挑构造函数
        services.AddSingleton(sp => new GoogleTranslator(
            sp.GetRequiredService<HttpClientProvider>(), sp.GetRequiredService<AppSettings>()));
        // 官方引擎（FR-024）：未配置 Key 时 IsConfigured 为 false，设置页显示「未配置」且不可选
        services.AddSingleton<TencentTranslator>();
        services.AddSingleton<BaiduTranslator>();
        // 国外引擎：代理归属为「仅国外引擎」作用域（13.1.1）
        services.AddSingleton<AzureTranslator>();
        services.AddSingleton<DeepLTranslator>();
        // AI 通道（FR-022）：OpenAI 兼容，需 BaseURL + Key + 模型名齐备才算已配置，默认不可选
        services.AddSingleton<LlmTranslator>();
        services.AddSingleton(sp => new EngineStatsRepository(database));
        services.AddSingleton(sp =>
        {
            var appSettings = sp.GetRequiredService<AppSettings>();
            var stats = sp.GetRequiredService<EngineStatsRepository>();
            // FR-037（批 2 / spec §4）：GlossaryEnabled=false 时给空表——词条保留、替换直通
            var glossary = new GlossaryCache(() => appSettings.GlossaryJson, () => appSettings.GlossaryEnabled);
            ITranslator Wrap(ITranslator inner) => new GlossaryTranslator(
                inner, glossary.Items,
                (id, outcome, err, latency) => stats.Record(id, outcome, err, latency),
                () => appSettings.PrivacyMode);
            // 顺序即设置页下拉顺序，且 Catalog 回落引擎 = 首个（Bing），与装饰前一致
            return new TranslatorCatalog(new ITranslator[]
            {
                Wrap(sp.GetRequiredService<BingTranslator>()),
                Wrap(sp.GetRequiredService<GoogleTranslator>()),
                Wrap(sp.GetRequiredService<TencentTranslator>()),
                Wrap(sp.GetRequiredService<BaiduTranslator>()),
                Wrap(sp.GetRequiredService<AzureTranslator>()),
                Wrap(sp.GetRequiredService<DeepLTranslator>()),
                Wrap(sp.GetRequiredService<LlmTranslator>()),
            });
        });

        // 划词取词器：取词期间需抑制剪贴板监听，否则本程序还原剪贴板会再次触发翻译（FR-017）
        services.AddSingleton<ClipboardMonitor>();
        services.AddSingleton<ITextCapturer>(sp => new ClipboardCapturer(
            message => Log.Debug("{CaptureLog}", message),
            suppressFor: sp.GetRequiredService<ClipboardMonitor>().Suppress));

        services.AddSingleton(database);
        services.AddSingleton<IHistoryRepository>(_ => new HistoryRepository(database));
        services.AddSingleton<IVocabularyRepository>(_ => new VocabularyRepository(database));
        // FR-035：Anki 直推客户端——专用 HttpClient 强制不经代理（127.0.0.1 被系统代理转发会假失败）
        services.AddSingleton(_ => new AnkiConnectClient(AnkiConnectClient.CreateLocalHostClient()));
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<HotkeyManager>();
        // FR-036：悬停取词的鼠标钩子与决策层（默认不安装，见 ApplyPrivacySideEffects 门控）
        services.AddSingleton<MouseButtonHook>();
        // FR-038：双击修饰键的键盘钩子（同一门控纪律；目标键经 Func 读取，设置改动即时生效）
        services.AddSingleton<KeyboardButtonHook>();
        services.AddSingleton<HoverBadgeWindow>();
        // FR-037：场景档案服务（对 AppSettings 单例做稀疏覆盖；落盘仍由 ISettingsStore 负责）
        services.AddSingleton(sp => new ProfileService(sp.GetRequiredService<AppSettings>()));
        // FR-046：本地 HTTP API 网关（设置 ↔ HttpListener 生命周期胶水；门控见 ApplyPrivacySideEffects）
        services.AddSingleton(sp => new LocalApiGateway(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<TranslatorCatalog>()));
        services.AddSingleton<AutoStart>();
        // FR-021 截图翻译：OCR 能力探测（Windows.Media.Ocr）+ 截屏/遮罩/识别流程编排。
        // FR-030（14.9.2）：识别按 OcrLocalEngine 分发 windows/paddle 双引擎；paddle 降级一次性气泡走同一回调
        services.AddSingleton(sp => new OcrService(
            sp.GetRequiredService<AppSettings>(),
            (title, message) => _trayIcon?.ShowNotification(title, message, NotificationIcon.Warning)));
        // FR-027 钉图：张数/像素上限判定与窗口登记（气泡回调在调用时读 _trayIcon，故注册顺序无关）
        services.AddSingleton(sp => new PinWindowManager(
            sp.GetRequiredService<AppSettings>(),
            (title, message) => _trayIcon?.ShowNotification(title, message, NotificationIcon.Warning)));
        services.AddSingleton(sp => new ScreenCaptureTranslateFlow(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<OcrService>(),
            sp.GetRequiredService<QuickWindow>(),
            sp.GetRequiredService<PinWindowManager>(),
            sp.GetRequiredService<TranslatorCatalog>(), // FR-027c：钉图按块翻译用同一套引擎与设置
            (title, message) => _trayIcon?.ShowNotification(title, message, NotificationIcon.Warning)));
        services.AddSingleton<QuickTranslateViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<QuickWindow>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        Log.Information("设置文件：{SettingsPath}，当前引擎：{Engine}", store.FilePath, settings.Engine);
    }

    /// <summary>按当前设置解析代理（FR-018）；密码经 DPAPI 解密后在内存中使用。</summary>
    private ProxyOptions? ResolveProxy(ProxyScope scope)
    {
        var settings = _services?.GetService<AppSettings>();
        if (settings is null || !settings.ProxyEnabled || string.IsNullOrWhiteSpace(settings.ProxyHost))
        {
            return null;
        }

        var modeAll = string.Equals(settings.ProxyMode, "all", StringComparison.OrdinalIgnoreCase);
        var applies = modeAll || scope == ProxyScope.GoogleOnly;
        if (!applies)
        {
            return null;
        }

        var password = SecretStore.Unprotect(settings.ProxyPasswordEncrypted) ?? "";
        return new ProxyOptions(
            settings.ProxyHost, settings.ProxyPort, settings.ProxyUserName, password, settings.ProxyScheme);
    }

    /// <summary>FR-001：注册全局热键，注册失败仅气泡提示，不影响其他功能。</summary>
    private void RegisterHotkeysFromSettings()
    {
        _hotkeyManager = _services!.GetRequiredService<HotkeyManager>();
        _hotkeyManager.HotkeyPressed += OnHotkeyPressed;

        var settings = _services!.GetRequiredService<AppSettings>();
        var failures = new List<string>();

        var inputDefinition = HotkeyDefinition.ParseOrDefault(settings.HotkeyInputTranslate, HotkeyDefinition.DefaultInput);
        if (!_hotkeyManager.TryRegister("input", inputDefinition))
        {
            failures.Add(inputDefinition.ToString());
        }

        var selectDefinition = HotkeyDefinition.ParseOrDefault(settings.HotkeySelectTranslate, HotkeyDefinition.DefaultSelect);
        if (!_hotkeyManager.TryRegister("select", selectDefinition))
        {
            failures.Add(selectDefinition.ToString());
        }

        // FR-021：截图翻译热键。语言包缺失时**仍然注册**，按下后走取消路径并提示（13.2.5），不静默失效
        var captureDefinition = HotkeyDefinition.ParseOrDefault(settings.HotkeyCaptureTranslate, HotkeyDefinition.DefaultCapture);
        if (!_hotkeyManager.TryRegister("capture", captureDefinition))
        {
            failures.Add(captureDefinition.ToString());
        }

        // FR-037（批 2）：场景档案循环切换热键（标准 → 阅读 → 隐私 → 自定义 → 标准）
        var profileDefinition = HotkeyDefinition.ParseOrDefault(settings.HotkeySwitchProfile, HotkeyDefinition.DefaultProfile);
        if (!_hotkeyManager.TryRegister("profile", profileDefinition))
        {
            failures.Add(profileDefinition.ToString());
        }

        if (failures.Count > 0)
        {
            _trayIcon!.ShowNotification(
                "速译 - 热键注册失败",
                $"以下热键可能被其他程序占用：{string.Join("、", failures)}。请在设置中修改。",
                NotificationIcon.Warning);
            Log.Warning("热键注册失败：{FailedHotkeys}", string.Join("、", failures));
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyManager.HotkeyPressedEventArgs e)
    {
        // HotkeyManager 在 UI 线程触发，无需调度
        Log.Debug("热键触发：{Name}", e.Name);
        if (e.Name == "input")
        {
            _services!.GetRequiredService<QuickWindow>().ToggleForInput();
        }
        else if (e.Name == "select")
        {
            _ = TranslateSelectionAsync();
        }
        else if (e.Name == "capture")
        {
            _ = _services!.GetRequiredService<ScreenCaptureTranslateFlow>().StartAsync();
        }
        else if (e.Name == "profile")
        {
            // FR-037：循环切换到下一个档案
            var profiles = _services!.GetRequiredService<ProfileService>();
            SwitchProfile(profiles.NextProfileName());
        }
    }

    /// <summary>
    /// FR-037：应用档案并跑副作用。档案只改 <see cref="AppSettings"/> 的 8 个键（Core <c>ProfileService</c>），
    /// 这里负责落盘 + 与托盘/设置页同一套启停门控（剪贴板监听、鼠标钩子、隐私）。
    /// </summary>
    private void SwitchProfile(string name)
    {
        var services = _services!;
        var profiles = services.GetRequiredService<ProfileService>();
        var settings = services.GetRequiredService<AppSettings>();

        profiles.Apply(name);
        services.GetRequiredService<ISettingsStore>().Save(settings);
        ApplyPrivacySideEffects(services, settings.PrivacyMode);
        _trayIcon!.ShowNotification("速译", $"已切换场景档案：{profiles.DisplayName}", NotificationIcon.Info);
        Log.Information("场景档案切换：{Profile}（偏离标记 {Deviation}）", profiles.DisplayName, profiles.IsDeviation());
    }

    /// <summary>托盘「场景档案」子菜单：展开时重建列表与勾选态（自定义档案可能已在设置页增删）。</summary>
    private void RebuildProfileMenu(MenuItem host)
    {
        var profiles = _services!.GetRequiredService<ProfileService>();
        host.Items.Clear();
        foreach (var name in new[] { ProfileService.StandardName }
                     .Concat(profiles.AllProfiles().Select(p => p.Name)))
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = profiles.DisplayName == name,
            };
            var captured = name;
            item.Click += (_, _) => SwitchProfile(captured);
            host.Items.Add(item);
        }
    }

    /// <summary>
    /// FR-036（spec §2.2）：悬停取词接线。钩子只观察不拦截；显隐判定在 Core <c>HoverTriggerLogic</c>；
    /// 浮标点击复用 Alt+S 的 <see cref="TranslateSelectionAsync"/>，不新建第二条取词链路。
    /// 开关（含隐私模式豁免）在 <see cref="ApplyPrivacySideEffects"/>，这里只连事件与初始状态。
    /// </summary>
    private void InitHoverSelect()
    {
        var services = _services!;
        var settings = services.GetRequiredService<AppSettings>();
        var hook = services.GetRequiredService<MouseButtonHook>();
        var badge = services.GetRequiredService<HoverBadgeWindow>();

        _hoverLogic = new HoverTriggerLogic(() => Environment.TickCount64);
        badge.BadgeClicked += () => _ = TranslateSelectionAsync();

        hook.LeftButtonDown += (x, y, self) =>
        {
            _hoverLogic.OnMouseDown(x, y, self);
            // 任意鼠标按下即收起浮标；点向浮标自身的那一下必须放过（钩子事件先于窗口点击到达）
            if (!badge.HitTestBadge(x, y))
            {
                Dispatcher.BeginInvoke(badge.HideBadge);
            }
        };
        hook.LeftButtonUp += (x, y, _, self) =>
        {
            if (_hoverLogic.OnMouseUp(x, y, self))
            {
                Dispatcher.BeginInvoke(() => badge.ShowAt(x, y));
            }
        };

        if (MouseHookNeeded(settings) && !settings.PrivacyMode)
        {
            hook.Start();
            Log.Information("鼠标观察钩子已安装（悬停取词 / 侧键映射任一开启）");
        }

        // FR-039（批 3）：侧键抬起 = 划词(X1)/截图(X2)。前台是本程序时忽略；
        // 钩子只观察不拦截——侧键的浏览器前进/后退照常发生，卡片文案如实写明。
        hook.XButtonUp += (button, self) =>
        {
            if (self)
            {
                return;
            }

            var current = _services!.GetRequiredService<AppSettings>();
            if (button == 1 && current.MouseSideButtonSelect)
            {
                Dispatcher.BeginInvoke(() => _ = TranslateSelectionAsync());
            }
            else if (button == 2 && current.MouseSideButtonCapture)
            {
                Dispatcher.BeginInvoke(() => _ = _services!.GetRequiredService<ScreenCaptureTranslateFlow>().StartAsync());
            }
        };
    }

    /// <summary>
    /// FR-038（批 3 / spec §1）：双击修饰键划词。键盘钩子只观察；判定在 Core
    /// <see cref="ModifierKeyDoubleTapDetector"/>（250ms 窗口、混键作废、500ms 冷却）。
    /// 目标键经 Func 每次现读 <c>DoubleTapKey</c>，设置改动即时生效、无需重建检测器。
    /// </summary>
    private void InitDoubleTap()
    {
        var services = _services!;
        var settings = services.GetRequiredService<AppSettings>();
        var hook = services.GetRequiredService<KeyboardButtonHook>();

        _doubleTap = new ModifierKeyDoubleTapDetector(
            () => Environment.TickCount64,
            () => DoubleTapVirtualKey(settings.DoubleTapKey),
            () => Dispatcher.BeginInvoke(() => _ = TranslateSelectionAsync()));

        hook.KeyDown += (vk, self) => _doubleTap.OnKeyDown(vk, self);
        hook.KeyUp += (vk, _) => _doubleTap.OnKeyUp(vk);

        if (settings.DoubleTapTranslateEnabled && !settings.PrivacyMode)
        {
            hook.Start();
            Log.Information("双击修饰键划词已启用（键盘钩子安装，目标键 {Key}）", settings.DoubleTapKey);
        }
    }

    /// <summary>双击目标键名 → 虚拟键码（未知值按 alt，与设置下拉可选集一致）。</summary>
    internal static int DoubleTapVirtualKey(string? key) => key switch
    {
        "ctrl" => 0x11,
        "shift" => 0x10,
        "win" => 0x5B,
        _ => 0x12,
    };

    /// <summary>FR-046（P0 批 4）：启动时按设置拉起本地 API（若启用且非隐私模式）。后续变更走设置 VM 同一 Apply。</summary>
    private void InitLocalApi()
    {
        var services = _services!;
        var settings = services.GetRequiredService<AppSettings>();
        services.GetRequiredService<TranslationApp.Services.LocalApiGateway>()
            .Apply(settings.LocalApiEnabled && !settings.PrivacyMode);
    }

    /// <summary>
    /// FR-040（批 3 / spec §3）：粘贴即译。小窗输入框为空时 Ctrl+V 由 QuickWindow 拦下并带着
    /// 剪贴板文本回调到这里——复用划词的「清洗→翻译」入口，不新建链路，也不新增剪贴板监听。
    /// </summary>
    private void InitPasteTranslate()
    {
        var quickWindow = _services!.GetRequiredService<QuickWindow>();
        quickWindow.PasteTranslateRequested += (_, text) =>
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                quickWindow.ShowForInput("剪贴板里没有文本");
                return;
            }

            quickWindow.ShowForSelection(PrepareCapturedText(text, out var cleaned), cleaned: cleaned);
        };
    }

    /// <summary>
    /// FR-021：初始化截图翻译。除了接好小窗按钮入口外，
    /// 启动时检测 OCR 语言包（13.2.5）：缺失时在设置页与托盘两处给出同一份安装指引。
    /// </summary>
    private void InitScreenCapture()
    {
        var ocr = _services!.GetRequiredService<OcrService>();
        _services!.GetRequiredService<QuickWindow>().CaptureRequested +=
            (_, _) => _ = _services!.GetRequiredService<ScreenCaptureTranslateFlow>().StartAsync();

        if (ocr.IsAvailable)
        {
            Log.Information("截图翻译可用：OCR 语言包 {Count} 个", ocr.AvailableLanguages.Count);
            return;
        }

        Log.Warning("系统未安装 OCR 语言包，截图翻译不可用（热键仍注册，按下后提示安装指引）");
        _trayIcon!.ShowNotification(
            "速译 - 截图翻译不可用", OcrLanguages.MissingPackMessage, NotificationIcon.Warning);
    }

    /// <summary>
    /// FR-028：把托盘气泡回调注入小窗外，VM 不依赖托盘。
    /// 累计多次降级只**建议**用户改默认引擎（一次性气泡），不新增可点击控件、更不代改设置（14.4.2）。
    /// </summary>
    private void InitEngineFallback()
    {
        var vm = _services!.GetRequiredService<QuickTranslateViewModel>();
        vm.NotifyBalloon =
            (title, message) => _trayIcon?.ShowNotification(title, message, NotificationIcon.Info);
        // FR-035：Anki 推送在后台线程完成，回 UI 线程只改状态行文本（不参与翻译时序，无落定延迟）
        vm.NotifyAnkiPushed = (added, reason) =>
            Dispatcher.BeginInvoke(() => vm.AppendAnkiResult(added, reason));
    }

    /// <summary>
    /// P0 批 1 / spec §3.2：划词与剪贴板自动翻译的文本先过阅读清洗（开关 CleanClipboardText，默认开）；
    /// 手动输入路径不经过这里。cleaned 回传「确实改动了文本」用于状态行提示。
    /// </summary>
    private string PrepareCapturedText(string text, out bool cleaned)
    {
        cleaned = false;
        try
        {
            if (!_services!.GetRequiredService<AppSettings>().CleanClipboardText) return text;
            var result = TextCleaner.CleanForReading(text);
            cleaned = result != text;
            return result;
        }
        catch
        {
            return text; // 清洗异常绝不影响取词链路
        }
    }

    /// <summary>
    /// FR-005 划词翻译：先取词（模拟 Ctrl+C，必须在小窗获得焦点之前完成，否则选区会丢失），
    /// 再显示小窗并自动翻译；取词失败优雅降级为手动输入模式。
    /// </summary>
    private async Task TranslateSelectionAsync()
    {
        var quickWindow = _services!.GetRequiredService<QuickWindow>();
        try
        {
            var capturer = _services!.GetRequiredService<ITextCapturer>();
            var text = await capturer.CaptureSelectedTextAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                quickWindow.ShowForInput("未取到选中文本，请手动输入（或该应用不支持模拟复制）");
                return;
            }

            quickWindow.ShowForSelection(PrepareCapturedText(text, out var cleaned), cleaned: cleaned);
        }
        catch (Exception ex)
        {
            // 取词异常绝不冒泡（需求 6 可靠性）
            Log.Error(ex, "划词取词失败，降级为手动输入模式");
            quickWindow.ShowForInput("取词失败，请手动输入");
        }
    }

    private void InitTray()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "速译",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
        };

        // 右键菜单（FR-008）：输入翻译 / 划词翻译 / 设置 / 退出
        var inputItem = new MenuItem { Header = "输入翻译" };
        inputItem.Click += (_, _) => ShowQuickInput();

        var selectItem = new MenuItem { Header = "划词翻译" };
        selectItem.Click += (_, _) => _ = TranslateSelectionAsync();

        // FR-021：截图翻译（与热键同一入口，热键被占用时仍可从托盘触发）
        var captureItem = new MenuItem { Header = "截图翻译" };
        captureItem.Click += (_, _) => _ = _services!.GetRequiredService<ScreenCaptureTranslateFlow>().StartAsync();

        // FR-027：一键清理桌面（存在钉图时才可用，菜单展开时刷新状态）
        var closePinsItem = new MenuItem { Header = "关闭所有钉图" };
        closePinsItem.Click += (_, _) => _services?.GetService<PinWindowManager>()?.CloseAll();

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => ShowSettings();

        // FR-037（批 2）：场景档案子菜单（标准/内置/自定义，展开时重建勾选态）
        var profileMenu = new MenuItem { Header = "场景档案" };
        profileMenu.SubmenuOpened += (_, _) => RebuildProfileMenu(profileMenu);
        profileMenu.Items.Add(new MenuItem { Header = ProfileService.StandardName }); // 占位，展开时重建

        // P0 批 1 / spec §2.1：隐私模式快捷开关（勾选态跟随设置，切换即时生效并落盘）
        var appSettings = _services!.GetRequiredService<AppSettings>();
        var privacyItem = new MenuItem
        {
            Header = "隐私模式",
            IsCheckable = true,
            IsChecked = appSettings.PrivacyMode,
        };
        privacyItem.Click += (_, _) =>
        {
            var on = privacyItem.IsChecked;
            appSettings.PrivacyMode = on;
            _services!.GetRequiredService<ISettingsStore>().Save(appSettings);
            ApplyPrivacySideEffects(_services!, on);
            _trayIcon!.ShowNotification("速译",
                on ? "隐私模式已开启：不再写入历史与日志" : "隐私模式已关闭",
                NotificationIcon.Info);
        };

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(inputItem);
        menu.Items.Add(selectItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(closePinsItem);
        menu.Items.Add(profileMenu);
        menu.Items.Add(settingsItem);
        menu.Items.Add(privacyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) =>
        {
            closePinsItem.IsEnabled = _services?.GetService<PinWindowManager>()?.HasPins == true;
            privacyItem.IsChecked = appSettings.PrivacyMode; // 设置页可能改过，展开时同步
        };
        _trayIcon.ContextMenu = menu;

        // 左键双击托盘图标打开设置主窗口（FR-008）
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();

        _trayIcon.ForceCreate(); // 代码构建（不在 XAML 可视树中）需显式创建
    }

    private void ShowStartBalloonIfEnabled()
    {
        var settings = _services!.GetRequiredService<AppSettings>();
        if (settings.ShowStartBalloon)
        {
            _trayIcon!.ShowNotification("速译", BuildStartBalloonText(settings), NotificationIcon.Info);
        }
    }

    /// <summary>
    /// 启动气泡文案：三个热键均取自当前设置（解析失败回退默认值），
    /// 避免写死文案后用户改了热键、气泡仍显示旧键（FR-001/021）。
    /// </summary>
    internal static string BuildStartBalloonText(AppSettings settings) =>
        "速译已启动，"
        + $"{HotkeyText(settings.HotkeyInputTranslate, HotkeyDefinition.DefaultInput)} 输入翻译 / "
        + $"{HotkeyText(settings.HotkeySelectTranslate, HotkeyDefinition.DefaultSelect)} 划词翻译 / "
        + $"{HotkeyText(settings.HotkeyCaptureTranslate, HotkeyDefinition.DefaultCapture)} 截图翻译";

    private static string HotkeyText(string? text, HotkeyDefinition fallback) =>
        HotkeyDefinition.ParseOrDefault(text, fallback).ToString();

    private void ShowQuickInput(string? notice = null) =>
        _services!.GetRequiredService<QuickWindow>().ShowForInput(notice);

    /// <summary>
    /// FR-017：初始化剪贴板监听。启用状态下用户复制文本会自动弹出小窗翻译；
    /// 监听器自身已含 300ms 防抖、3s 限流与本程序写剪贴板的抑制，不会自我循环。
    /// </summary>
    private void InitClipboardMonitor(AppSettings settings)
    {
        var monitor = _services!.GetRequiredService<ClipboardMonitor>();
        monitor.TextCopied += OnClipboardTextCopied;

        if (settings.ClipboardMonitorEnabled && !settings.PrivacyMode)
        {
            monitor.Start();
            Log.Information("剪贴板监听已启用");
        }
        else if (settings.ClipboardMonitorEnabled)
        {
            Log.Information("隐私模式开启，剪贴板监听未启动");
        }
    }

    /// <summary>
    /// P0 批 1 / spec §2：隐私模式开关变更后同步副作用（剪贴板监听启停）。
    /// 托盘菜单与设置页共用；历史/统计/日志的门控在各自路径内读取最新值，无需回调。
    /// 批 2（FR-036）+ 批 3（FR-038/039）扩展：两个全局钩子——**隐私模式开启时绝不安装**（B5 红线）；
    /// 隐私关闭时鼠标钩子按「悬停 / 侧键X1 / 侧键X2 任一开」启停，键盘钩子按「双击开关」启停；
    /// Start/Stop 幂等，可放心重放。
    /// </summary>
    internal static void ApplyPrivacySideEffects(IServiceProvider services, bool on)
    {
        var monitor = services.GetService<ClipboardMonitor>();
        var settings = services.GetRequiredService<AppSettings>();
        if (on)
        {
            monitor?.Stop();
        }
        else if (settings.ClipboardMonitorEnabled)
        {
            monitor?.Start();
        }

        var mouseHook = services.GetService<MouseButtonHook>();
        var keyboardHook = services.GetService<KeyboardButtonHook>();
        if (on)
        {
            mouseHook?.Stop();
            keyboardHook?.Stop();
        }
        else
        {
            if (MouseHookNeeded(settings))
            {
                mouseHook?.Start();
            }
            else
            {
                mouseHook?.Stop();
            }

            if (settings.DoubleTapTranslateEnabled)
            {
                keyboardHook?.Start();
            }
            else
            {
                keyboardHook?.Stop();
            }
        }

        // FR-046（P0 批 4）：本地 HTTP API 与钩子同纪律——隐私模式开启时绝不监听
        services.GetService<TranslationApp.Services.LocalApiGateway>()
            ?.Apply(!on && settings.LocalApiEnabled);
    }

    /// <summary>鼠标观察钩子的安装条件：悬停取词或任一侧键映射开着就需要。</summary>
    internal static bool MouseHookNeeded(AppSettings settings) =>
        settings.HoverSelectEnabled || settings.MouseSideButtonSelect || settings.MouseSideButtonCapture;

    private void OnClipboardTextCopied(object? sender, string text)
    {
        // 监听器在后台线程触发，需切回 UI 线程操作窗口
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                if (_services!.GetRequiredService<AppSettings>().PrivacyMode)
                {
                    return; // 隐私模式：监听本就未启动，双保险（运行中开启隐私时立即生效）
                }

                Log.Debug("剪贴板监听触发翻译：{Length} 字符", text.Length);
                var quickWindow = _services!.GetRequiredService<QuickWindow>();
                quickWindow.ShowForSelection(PrepareCapturedText(text, out var cleaned), cleaned: cleaned);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "剪贴板监听处理失败");
            }
        }));
    }

    /// <summary>
    /// 启动后台预热当前引擎（Bing 首帧需抓令牌，预热后首次翻译明显更快）。
    /// 失败只记日志，不影响启动。
    /// </summary>
    private void WarmUpEngine()
    {
        var settings = _services!.GetRequiredService<AppSettings>();
        // P0 批 1：Resolve 返回的是 GlossaryTranslator 包装件，预热要剥壳拿真实 Bing 实例
        var engine = GlossaryTranslator.Unwrap(
            _services!.GetRequiredService<TranslatorCatalog>().Resolve(settings.Engine));
        if (engine is not BingTranslator bing)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            await bing.WarmUpAsync();
            Log.Debug("Bing 令牌预热完成，耗时 {Elapsed} ms", (DateTimeOffset.UtcNow - started).TotalMilliseconds);
        });
    }

    private void ShowSettings()
    {
        var window = _services!.GetRequiredService<MainWindow>();
        window.Show();
        window.Activate();
    }
}
