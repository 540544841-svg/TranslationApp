using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InitLogging(e.Args.Contains("--verbose"));
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
            RegisterHotkeysFromSettings();
            InitClipboardMonitor(settings);
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
        _services?.GetService<ITtsService>()?.Stop();
        _trayIcon?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        Log.Information("速译已退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>初始化滚动文件日志；--verbose 时输出 Debug 级（排障用）。</summary>
    private static void InitLogging(bool verbose)
    {
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
        services.AddSingleton<ITranslator, BingTranslator>();
        // FR-028：显式传入设置，让 Google 在开启自动降级时把网络重试降为 0
        // （最坏等待从 2×8s 收到 8s，8s 内即轮到 Bing）——写成工厂，不依赖容器挑构造函数
        services.AddSingleton<ITranslator>(sp => new GoogleTranslator(
            sp.GetRequiredService<HttpClientProvider>(), sp.GetRequiredService<AppSettings>()));
        // 官方引擎（FR-024）：未配置 Key 时 IsConfigured 为 false，设置页显示「未配置」且不可选
        services.AddSingleton<ITranslator, TencentTranslator>();
        services.AddSingleton<ITranslator, BaiduTranslator>();
        // 国外引擎：代理归属为「仅国外引擎」作用域（13.1.1）
        services.AddSingleton<ITranslator, AzureTranslator>();
        services.AddSingleton<ITranslator, DeepLTranslator>();
        // AI 通道（FR-022）：OpenAI 兼容，需 BaseURL + Key + 模型名齐备才算已配置，默认不可选
        services.AddSingleton<ITranslator, LlmTranslator>();
        services.AddSingleton<TranslatorCatalog>();

        // 划词取词器：取词期间需抑制剪贴板监听，否则本程序还原剪贴板会再次触发翻译（FR-017）
        services.AddSingleton<ClipboardMonitor>();
        services.AddSingleton<ITextCapturer>(sp => new ClipboardCapturer(
            message => Log.Debug("{CaptureLog}", message),
            suppressFor: sp.GetRequiredService<ClipboardMonitor>().Suppress));

        services.AddSingleton(database);
        services.AddSingleton<IHistoryRepository>(_ => new HistoryRepository(database));
        services.AddSingleton<IVocabularyRepository>(_ => new VocabularyRepository(database));
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<HotkeyManager>();
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
        _services!.GetRequiredService<QuickTranslateViewModel>().NotifyBalloon =
            (title, message) => _trayIcon?.ShowNotification(title, message, NotificationIcon.Info);
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

            quickWindow.ShowForSelection(text);
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

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(inputItem);
        menu.Items.Add(selectItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(closePinsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) => closePinsItem.IsEnabled = _services?.GetService<PinWindowManager>()?.HasPins == true;
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

        if (settings.ClipboardMonitorEnabled)
        {
            monitor.Start();
            Log.Information("剪贴板监听已启用");
        }
    }

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

                Log.Debug("剪贴板监听触发翻译：{Length} 字符", text.Length);
                _services!.GetRequiredService<QuickWindow>().ShowForSelection(text);
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
        var engine = _services!.GetRequiredService<TranslatorCatalog>().Resolve(settings.Engine);
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
