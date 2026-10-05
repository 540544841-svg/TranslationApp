using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TranslationApp.Controls;
using TranslationApp.Core.Anki;
using TranslationApp.Core.Backup;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Dictionary;
using TranslationApp.Core.Diagnostics;
using TranslationApp.Core.History;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Speech;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using TranslationApp.Core.Updates;
using TranslationApp.Interop;
using TranslationApp.Services;
using TranslationApp.Theming;
using TranslationApp.ViewModels;
using TranslationApp.Windows;

namespace TranslationApp;

/// <summary>
/// 应用入口：Serilog 初始化 → 全局异常处理 → 单实例检查 → DI 组装 →
/// 托盘初始化 → 全局热键注册（FR-001/008）。
/// 手动启动落在工作台（首启先播一次「启印」）；带 --minimized 时静默驻托盘。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private ServiceProvider? _services;
    private TaskbarIcon? _trayIcon;
    private HotkeyManager? _hotkeyManager;
    private HoverTriggerLogic? _hoverLogic;
    private ModifierKeyDoubleTapDetector? _doubleTap;
    private string? _lastActionableIssueArea;
    private string? _lastActionableIssueDetail;
    private string? _lastActionableIssueSection;
    private ReplacementToastWindow? _replacementToast;

    /// <summary>键盘钩子是否已记过「收到首个事件」日志（只记一次，避免每次按键写日志）。</summary>
    private bool _keyboardHookEventLogged;
    private bool _replacementInProgress;
    private bool _verboseStartup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // P0「OCR 原生引擎进程隔离」：同一 EXE 以 --ocr-worker <管道名> 自启动的**子进程模式**。
        // 必须在任何其他初始化之前分流：不取单实例互斥体（否则会被主进程挡住）、不建托盘、不注册热键。
        if (OcrWorkerProtocol.TryParseWorkerArguments(e.Args, out var ocrWorkerPipe))
        {
            StartOcrWorker(ocrWorkerPipe);
            return;
        }
        InitLogging(e.Args.Contains("--verbose"));
        _verboseStartup = e.Args.Contains("--verbose");
        RegisterGlobalExceptionHandlers();

        // 开发/QA：离屏渲染模式。必须在单实例守卫之前分流——它不显示任何窗口、不注册热键，
        // 是无 UI 的独立进程，不该被用户正在使用的实例挡住。
        if (TryGetArgValue(e.Args, "--render-ui", out var renderDir))
        {
            BuildServices();
            var renderSettings = _services!.GetRequiredService<AppSettings>();
            ThemeManager.ConfigurePaperHours(renderSettings.ThemePaperFromHour, renderSettings.ThemePaperToHour);
            ThemeManager.Apply(SettingsViewModel.ToAppTheme(renderSettings.Theme));
            RenderUiScreens(renderDir);
            return;
        }

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

        // 开机自启自愈：单文件版被挪过位置后，注册表里的旧路径会让自启静默失效，每次启动顺手校正。
        try
        {
            _services!.GetRequiredService<AutoStart>().RepairIfEnabled();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "开机自启自愈失败");
        }

        // 主题需在窗口创建前应用，避免首帧闪烁（FR-019）
        var settings = _services!.GetRequiredService<AppSettings>();
        ThemeManager.ConfigurePaperHours(settings.ThemePaperFromHour, settings.ThemePaperToHour);
        ThemeManager.Apply(SettingsViewModel.ToAppTheme(settings.Theme));

        var trayReady = false;
        var revealScheduled = false;
        // 开机自启带 --minimized：不抢屏幕，只回托盘弹一枚气泡。
        var startSilent = e.Args.Contains("--minimized");

        try
        {
            trayReady = TryInitTray();
            // 只在静默启动（开机自启）时用气泡自报家门；手动启动有工作台本身，再弹一枚就是吵。
            if (trayReady && startSilent)
            {
                ShowStartBalloonIfEnabled();
            }
            // FR-059：首次运行弹一次上手卡（延后一拍，不与启动气泡挤在同一帧）
            var store = _services!.GetRequiredService<ISettingsStore>();
            // 静默启动（开机自启）不弹任何东西：启印与立契都留给用户自己打开的那一次。
            var needsGuide = !settings.OnboardingShown && !startSilent;
            if (settings.LaunchRevealEnabled && !startSilent)
            {
                // 「启印」先跑，立契等它收层后再接，两层不叠在一起。
                revealScheduled = true;
                ScheduleLaunchReveal(needsGuide ? () => ScheduleOnboardingCard(settings, store) : null);
            }
            else if (needsGuide)
            {
                ScheduleOnboardingCard(settings, store);
            }

            if (_verboseStartup && settings.PrivacyMode)
            {
                // spec §2.2：用户明确要了 --verbose 但隐私模式压住了日志，必须说明而不是静默失效
                ShowTrayNotification("隐私模式", "开启中，--verbose 日志不落盘", NotificationIcon.Info);
            }
            RegisterHotkeysFromSettings();
            InitClipboardMonitor(settings);
            InitHoverSelect();
            InitDoubleTap();
            InitPasteTranslate();
            InitLocalApi();
            InitScreenCapture();
            InitEngineFallback();
            if (settings.UpdateAutoCheck && !string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
            {
                _ = CheckForUpdatesSilentlyAsync(settings);
            }
            WarmUpEngine();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "核心功能初始化失败，应用退出");
            Shutdown(1);
            return;
        }

        // --settings 显式打开设置；托盘创建失败时也必须给用户一个可见入口。
        // 复核入口：--onboarding 摆出「立契」，--launch-reveal 再演一次「启印」。
        // 两者都只上屏，不碰设置——不会把「看过」标记改回去，也不会顺手打开自启。
        if (e.Args.Contains("--onboarding"))
        {
            ShowOnboardingPreview();
        }

        if (e.Args.Contains("--launch-reveal") && !revealScheduled)
        {
            revealScheduled = true;
            ScheduleLaunchReveal(null);
        }

        // 手动启动即落在工作台：主窗体既是启印动画的舞台，也是用户的第一落点。
        // 不在这里开窗的三种情况：--minimized（开机自启不抢屏幕）、
        // 启印已经替我们把窗开好（revealScheduled）、--onboarding 自己会摆出立契。
        var wantWorkbench = !startSilent && !revealScheduled && !e.Args.Contains("--onboarding");
        if (e.Args.Contains("--settings") || !trayReady || wantWorkbench)
        {
            ShowSettings();
        }

        Log.Information("译印启动完成（{Mode}，PID {Pid}）",
            !trayReady ? "设置窗口模式" : startSilent ? "静默驻托盘" : "工作台", Environment.ProcessId);

        // 诊断在启动时自动跑一次：设置窗口打开时清单已经是有结果的，不必等用户点「重新诊断」。
        // 放在启动日志之后，不跟托盘 / 热键 / 窗口抢首帧；失败只记日志。
        _ = RunStartupDiagnosticsAsync();
    }

    /// <summary>
    /// 后台跑一次 Doctor。诊断页的清单先以「待检查」铺好，跑完由 ReportReady 换成结论，
    /// 所以这里既不阻塞启动，也不需要设置窗口在场。
    /// </summary>
    private async Task RunStartupDiagnosticsAsync()
    {
        try
        {
            var doctor = _services?.GetRequiredService<DoctorService>();
            if (doctor is not null)
            {
                var report = await doctor.RunAsync();
                Log.Information("启动自动诊断完成：{Summary}", report.SummaryText);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动自动诊断失败（忽略）");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // FR-001/008：退出时注销热键、释放托盘图标、监听器与互斥体
        _hotkeyManager?.Dispose();
        _services?.GetService<PinWindowManager>()?.CloseAll(); // FR-027：退出前释放钉图位图
        _replacementToast?.Close();
        _services?.GetService<ClipboardMonitor>()?.Dispose();
        _services?.GetService<MouseButtonHook>()?.Dispose(); // FR-036/039：卸载鼠标钩子
        _services?.GetService<KeyboardButtonHook>()?.Dispose(); // FR-038：卸载键盘钩子
        _services?.GetService<TranslationApp.Services.LocalApiGateway>()?.Stop(); // FR-046：关监听
        _services?.GetService<ITtsService>()?.Stop();
        _trayIcon?.Dispose();
        _services?.Dispose();
        _singleInstance?.Dispose();
        Log.Information("译印已退出");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>启动后静默检查一次签名更新；失败只记日志，不打扰用户。</summary>
    private async Task CheckForUpdatesSilentlyAsync(AppSettings settings)
    {
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);
            var current = new ReleaseVersion(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
            var services = _services;
            if (services is null) return;

            var result = await services.GetRequiredService<UpdateService>()
                .CheckAsync(settings.UpdateManifestUrl, current);
            settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
            services.GetRequiredService<ISettingsStore>().Save(settings);
            if (result.HasUpdate)
            {
                ShowTrayNotification("更新可用", result.Message, NotificationIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "后台检查更新失败");
            SetActionableIssue("更新通道", $"后台检查失败：{ex.Message}", "updates");
        }
    }

    /// <summary>
    /// OCR 隔离进程的**子进程入口**（P0「OCR 原生引擎进程隔离」）：只跑命名管道服务循环，
    /// 不建托盘/不注册热键/不开 UI/不取单实例互斥体。管道断开（主进程退出或被强杀）即自行结束。
    /// 放到后台线程执行，启动线程只是设好日志并返回，让 WPF 调度器保持可响应，以便收尾时能正常退出。
    /// </summary>
    private void StartOcrWorker(string pipeName)
    {
        InitWorkerLogging();
        Log.Information(
            "=== OCR 隔离进程启动（PID {Pid}，管道 {Pipe}）===", Environment.ProcessId, pipeName);

        _ = Task.Run(async () =>
        {
            try
            {
                await OcrWorkerHost.RunAsync(pipeName);
                Log.Information("OCR 隔离进程正常退出（PID {Pid}）", Environment.ProcessId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OCR 隔离进程异常退出");
            }
            finally
            {
                Log.CloseAndFlush();
                _ = Dispatcher.BeginInvoke(new Action(() => Shutdown(0)));
            }
        });
    }

    /// <summary>
    /// 隔离进程独立日志（<c>ocr-worker-*.log</c>）：与主进程日志同一目录、同一隐私纪律，
    /// 但分开文件便于把「原生库崩了」与「主进程正常」分开排查。
    /// </summary>
    private static void InitWorkerLogging()
    {
        if (PrivacyPeek())
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
            return;
        }

        var logDir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDir, "ocr-worker-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3,
                shared: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static void InitLogging(bool verbose)
    {
        if (PrivacyPeek())
        {
            Log.Logger = new LoggerConfiguration().CreateLogger(); // 无 sink：启动即不写任何日志文件
            return;
        }

        var logDir = AppPaths.LogsDirectory;
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

        Log.Information("=== 译印启动（PID {Pid}）===", Environment.ProcessId);
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
        var databasePath = AppPaths.HistoryDatabaseFile;
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
        services.AddSingleton<IClipboardSnapshotProvider, WpfClipboardSnapshotProvider>();
        services.AddSingleton<InPlaceTranslationService>();
        services.AddSingleton(sp => new SelectionReplacementService(
            sp.GetRequiredService<IClipboardSnapshotProvider>(),
            sp.GetRequiredService<ClipboardMonitor>().Suppress));
        services.AddSingleton<ITextCapturer>(sp => new ClipboardCapturer(
            message => Log.Debug("{CaptureLog}", message),
            suppressFor: sp.GetRequiredService<ClipboardMonitor>().Suppress,
            snapshotProvider: sp.GetRequiredService<IClipboardSnapshotProvider>()));

        services.AddSingleton(database);
        services.AddSingleton(sp => new BackupService(AppPaths.DataDirectory, database));
        var updateHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        services.AddSingleton(new UpdateService(() => updateHttpClient));
        services.AddSingleton<IHistoryRepository>(_ => new HistoryRepository(database));
        services.AddSingleton<IVocabularyRepository>(_ => new VocabularyRepository(database));
        // FR-035：Anki 直推客户端——专用 HttpClient 强制不经代理（127.0.0.1 被系统代理转发会假失败）
        services.AddSingleton(_ => new AnkiConnectClient(AnkiConnectClient.CreateLocalHostClient()));
        // FR-049：mdx 离线词典（%AppData%\TranslationApp\dicts）。构造不扫描磁盘，首次查词/列示才解析，
        // 词典开关关闭时连解析都不做；只用本地文件，不产生任何网络请求。
        services.AddSingleton(sp => new DictionaryManager(
            AppPaths.DictionariesDirectory,
            enabledProvider: () => sp.GetRequiredService<AppSettings>().DictionariesEnabled));
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<HotkeyManager>();
        // FR-036：悬停取词的鼠标钩子与决策层（默认不安装，见 ApplyPrivacySideEffects 门控）
        services.AddSingleton<MouseButtonHook>();
        // FR-036：选区探针——浮标只在「确认选中了文字」时才落印（UIA 属 App 层，Core 只认这个口子）
        services.AddSingleton<ISelectionProbe, UiaSelectionProbe>();
        // FR-038：双击修饰键的键盘钩子（同一门控纪律；目标键经 Func 读取，设置改动即时生效）
        services.AddSingleton<KeyboardButtonHook>();
        services.AddSingleton<HoverBadgeWindow>();
        // FR-037：场景模式服务（对 AppSettings 单例做稀疏覆盖；落盘仍由 ISettingsStore 负责）
        services.AddSingleton(sp => new ProfileService(sp.GetRequiredService<AppSettings>()));
        // FR-046：本地 HTTP API 网关（设置 ↔ HttpListener 生命周期胶水；门控见 ApplyPrivacySideEffects）
        services.AddSingleton(sp => new LocalApiGateway(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<TranslatorCatalog>()));
        services.AddSingleton<AutoStart>();
        // FR-021 截图翻译：OCR 能力探测（Windows.Media.Ocr）+ 截屏/遮罩/识别流程编排。
        // FR-030（14.9.2）：识别按 OcrLocalEngine 分发 windows/paddle 双引擎；paddle 降级一次性气泡走同一回调
        // P0（OCR 原生引擎进程隔离）：paddle 跑在同 EXE 自启动的子进程里，这里是它的进程/管道管理器。
        // 注册为单例以便容器卸载时收尾（正常路径下子进程会因管道断开而自行退出）。
        services.AddSingleton<OcrWorkerLauncher>();
        services.AddSingleton(sp => new OcrService(
            sp.GetRequiredService<AppSettings>(),
            (title, message) => ShowTrayNotification(title, message, NotificationIcon.Warning),
            sp.GetRequiredService<OcrWorkerLauncher>()));
        // Doctor：集中诊断热键、OCR、代理、数据库与更新通道；只在设置页命令触发。
        services.AddSingleton<DoctorService>();
        // FR-027 钉图：张数/像素上限判定与窗口登记（气泡回调在调用时读 _trayIcon，故注册顺序无关）
        services.AddSingleton(sp => new PinWindowManager(
            sp.GetRequiredService<AppSettings>(),
            (title, message) => ShowTrayNotification(title, message, NotificationIcon.Warning)));
        services.AddSingleton<FeedbackSoundService>();
        services.AddSingleton(sp => new ScreenCaptureTranslateFlow(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<OcrService>(),
            sp.GetRequiredService<QuickWindow>(),
            sp.GetRequiredService<PinWindowManager>(),
            sp.GetRequiredService<TranslatorCatalog>(), // FR-027c：钉图按块翻译用同一套引擎与设置
            (title, message) => ShowTrayNotification(title, message, NotificationIcon.Warning),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<FeedbackSoundService>()));
        services.AddSingleton<QuickWindow>();
        services.AddSingleton<QuickTranslateViewModel>();
        services.AddSingleton<SettingsViewModel>();
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

        // FR-037（批 2）：场景模式循环切换热键（标准 → 阅读 → 隐私 → 自定义 → 标准）
        var profileDefinition = HotkeyDefinition.ParseOrDefault(settings.HotkeySwitchProfile, HotkeyDefinition.DefaultProfile);
        if (!_hotkeyManager.TryRegister("profile", profileDefinition))
        {
            failures.Add(profileDefinition.ToString());
        }

        if (settings.ReplaceSelectionEnabled && !string.IsNullOrWhiteSpace(settings.HotkeyReplaceTranslate))
        {
            var replaceDefinition = HotkeyDefinition.ParseOrDefault(
                settings.HotkeyReplaceTranslate, new HotkeyDefinition(HotkeyModifiers.Alt, 0x52));
            if (!_hotkeyManager.TryRegister("replace", replaceDefinition))
            {
                failures.Add(replaceDefinition.ToString());
            }
        }

        if (failures.Count > 0)
        {
            ShowTrayNotification(
                "热键注册失败",
                $"以下热键可能被其他程序占用：{string.Join("、", failures)}。请在设置中修改。",
                NotificationIcon.Warning);
            Log.Warning("热键注册失败：{FailedHotkeys}", string.Join("、", failures));
            SetActionableIssue(
                "热键", $"以下组合可能被占用：{string.Join("、", failures)}", "hotkeys");
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
        else if (e.Name == "replace")
        {
            _ = TranslateAndReplaceSelectionAsync();
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
        ShowTrayNotification("场景模式", $"已切换到「{profiles.DisplayName}」", NotificationIcon.Info);
        Log.Information("场景模式切换：{Profile}（偏离标记 {Deviation}）", profiles.DisplayName, profiles.IsDeviation());
    }

    /// <summary>托盘「场景模式」子菜单：展开时重建列表与勾选态（自定义档案可能已在设置页增删）。</summary>
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
    /// 托盘操作中心：展开时重建状态行与修复入口。状态取自实时对象（设置/引擎/热键管理器），
    /// 问题取自最近一次实际失败，因此这里显示的每条都能追溯到刚刚发生过的事。
    /// </summary>
    private void RebuildStatusMenu(MenuItem host)
    {
        host.Items.Clear();
        var snapshot = ReadOperationalSnapshot();
        host.Items.Add(NewDisabledItem($"引擎：{snapshot.Engine}"));
        host.Items.Add(NewDisabledItem($"档案：{snapshot.Profile}"));
        host.Items.Add(NewDisabledItem($"隐私模式：{snapshot.Privacy}"));
        host.Items.Add(NewDisabledItem($"热键：{snapshot.Hotkeys}"));

        if (_lastActionableIssueArea is null)
        {
            host.Items.Add(NewDisabledItem("最近暂无待处理问题"));
        }
        else
        {
            host.Items.Add(NewDisabledItem($"待处理 · {_lastActionableIssueArea}"));
            host.Items.Add(NewDisabledItem(_lastActionableIssueDetail ?? ""));
            var section = _lastActionableIssueSection ?? "diagnostics";
            var fixLabel = section switch
            {
                "hotkeys" => "修复热键",
                "updates" => "打开更新与数据",
                _ => "打开诊断",
            };
            var fixItem = new MenuItem { Header = fixLabel };
            var captured = section;
            fixItem.Click += (_, _) => OpenSettingsSection(captured);
            host.Items.Add(fixItem);
        }

        host.Items.Add(new Separator());
        var doctorItem = new MenuItem { Header = "打开 Doctor 诊断中心" };
        doctorItem.Click += (_, _) => OpenSettingsSection("diagnostics");
        host.Items.Add(doctorItem);

        var settingsEntry = new MenuItem { Header = "打开设置" };
        settingsEntry.Click += (_, _) => ShowSettings();
        host.Items.Add(settingsEntry);
    }

    private static MenuItem NewDisabledItem(string header) => new()
    {
        Header = header,
        IsEnabled = false,
    };

    private void OpenSettingsSection(string section)
    {
        ShowSettings();
        _services?.GetRequiredService<MainWindow>().NavigateToSection(section);
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

        // 判定拆两段（见 HoverTriggerLogic）：探针在这里注入，Core 那边不认识 UI Automation。
        _hoverLogic = new HoverTriggerLogic(
            () => Environment.TickCount64,
            services.GetRequiredService<ISelectionProbe>());
        badge.BadgeClicked += () => _ = TranslateSelectionAsync();

        hook.LeftButtonDown += (x, y, self) =>
        {
            // 点向浮标自身的那一下是「去翻译」，不是在选词：钩子事件先于窗口点击到达，
            // 所以这一下既不能收浮标，也不能进选词判定（否则抬起时浮标原地重落一枚）。
            if (badge.HitTestBadge(x, y))
            {
                _hoverLogic.IgnorePress();
                return;
            }

            // 其余任意鼠标按下即收起浮标
            _hoverLogic.OnMouseDown(x, y, self);
            Dispatcher.BeginInvoke(badge.HideBadge);
        };
        hook.LeftButtonUp += (x, y, tickMs, self) =>
        {
            if (!_hoverLogic.TryAcceptMouseUp(x, y, self))
            {
                return;
            }

            // 探针是跨进程调用，绝不能占着钩子线程：这里只起一个任务，落印时再回 UI 线程
            _ = ConfirmHoverBadgeAsync(badge, x, y);
        };

        if (MouseHookNeeded(settings) && !settings.PrivacyMode)
        {
            hook.Start();
            if (hook.IsActive)
            {
                Log.Information("鼠标观察钩子已安装（悬停取词 / 侧键映射任一开启）");
            }
            else
            {
                Log.Warning("鼠标观察钩子安装失败：侧键与悬停取词不可用（Win32 {Error}）", hook.LastStartError);
            }
        }

        // FR-039（批 3）：侧键抬起 = 划词(X1)/截图(X2)。前台是本程序时忽略；
        // 钩子只观察不拦截——侧键的浏览器前进/后退照常发生，卡片文案如实写明。
        hook.MouseMessageCodeObserved += code =>
            Log.Information("鼠标钩子收到消息 0x{Code:X4}{Side}",
                code, code is 0x020B or 0x020C ? "（侧键）" : string.Empty);

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
    /// <summary>
    /// 悬停取词的第二段：等选区探针的结论（探针跑在别的线程上，钩子线程必须立刻返回），
    /// 只有确认「选中了文字」才落印；落印一律回 UI 线程。
    /// </summary>
    private async Task ConfirmHoverBadgeAsync(HoverBadgeWindow badge, int x, int y)
    {
        if (_hoverLogic is null)
        {
            return;
        }

        bool show;
        try
        {
            show = await _hoverLogic.ConfirmAsync(x, y).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "悬停取词判定失败（忽略）");
            return;
        }

        if (show)
        {
            badge.Dispatcher.Invoke(() => badge.ShowAt(x, y));
        }
    }

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
            () =>
            {
                Log.Information("双击修饰键命中，触发划词翻译");
                Dispatcher.BeginInvoke(() => _ = TranslateSelectionAsync());
            });

        hook.KeyDown += (vk, self) =>
        {
            if (!_keyboardHookEventLogged)
            {
                // 只记「收到过第一个按键事件」，绝不记键码/内容（键盘钩子落任何键值都是隐私事故）
                _keyboardHookEventLogged = true;
                Log.Information("键盘钩子已收到首个按键事件（双击判定开始工作）");
            }

            TraceDoubleTapEdge(settings, vk, self, "按下");
            _doubleTap.OnKeyDown(vk, self);
        };
        hook.KeyUp += (vk, self) =>
        {
            TraceDoubleTapEdge(settings, vk, self, "抬起");
            _doubleTap.OnKeyUp(vk);
        };

        if (settings.DoubleTapTranslateEnabled && !settings.PrivacyMode)
        {
            hook.Start();
            if (hook.IsActive)
            {
                Log.Information("双击修饰键划词已启用（键盘钩子安装，目标键 {Key}）", settings.DoubleTapKey);
            }
            else
            {
                Log.Warning("键盘钩子安装失败：双击修饰键划词不可用（Win32 {Error}）", hook.LastStartError);
            }
        }
    }

    /// <summary>
    /// 双击判定的诊断轨迹（只在 --verbose 下可见）：仅记目标修饰键的边沿与前台归属，
    /// 不记任何其他键码、更不记文本——判定卡在哪一步必须能看见，但按键内容绝不进日志。
    /// </summary>
    private void TraceDoubleTapEdge(AppSettings settings, int vk, bool self, string edge)
    {
        if (!_verboseStartup || vk != DoubleTapVirtualKey(settings.DoubleTapKey))
        {
            return;
        }

        Log.Debug("双击判定：目标键 {Edge}（前台是本程序={Self}）", edge, self);
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

            quickWindow.ShowForSelection(PrepareCapturedText(text, out var cleaned), cleaned: cleaned,
                origin: QuickWindow.OriginClipboard);
        };
    }

    /// <summary>
    /// FR-021：初始化截图翻译（入口只有全局热键与托盘菜单）。
    /// 启动时检测 OCR 语言包（13.2.5）：缺失时在设置页与托盘两处给出同一份安装指引。
    /// </summary>
    private void InitScreenCapture()
    {
        var ocr = _services!.GetRequiredService<OcrService>();

        if (ocr.IsAvailable)
        {
            Log.Information("截图翻译可用：OCR 语言包 {Count} 个", ocr.AvailableLanguages.Count);
            return;
        }

        Log.Warning("系统未安装 OCR 语言包，截图翻译不可用（热键仍注册，按下后提示安装指引）");
        ShowTrayNotification(
            "截图翻译不可用", OcrLanguages.MissingPackMessage, NotificationIcon.Warning);
        SetActionableIssue("OCR 语言包", OcrLanguages.MissingPackMessage, "diagnostics");
    }

    /// <summary>
    /// FR-028：把托盘气泡回调注入小窗外，VM 不依赖托盘。
    /// 累计多次降级只**建议**用户改默认引擎（一次性气泡），不新增可点击控件、更不代改设置（14.4.2）。
    /// </summary>
    private void InitEngineFallback()
    {
        var vm = _services!.GetRequiredService<QuickTranslateViewModel>();
        vm.NotifyBalloon =
            (title, message) => ShowTrayNotification(title, message, NotificationIcon.Info);
        // FR-035：Anki 推送在后台线程完成，回 UI 线程只改状态行文本（不参与翻译时序，无落定延迟）
        vm.NotifyAnkiPushed = (added, reason) =>
            Dispatcher.BeginInvoke(() => vm.AppendAnkiResult(added, reason));
    }

    /// <summary>
    /// P0 批 1 / spec §3.2：划词与剪贴板自动翻译的文本先过自动整理换行（开关 CleanClipboardText，默认开）；
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

    /// <summary>取词、翻译并直接粘贴回原选区；任何一步失败都降级到普通小窗。</summary>
    private async Task TranslateAndReplaceSelectionAsync()
    {
        if (_replacementInProgress)
        {
            return;
        }

        var targetWindow = NativeMethods.GetForegroundWindow();
        _replacementInProgress = true;
        try
        {
            var captured = await _services!.GetRequiredService<ITextCapturer>().CaptureSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(captured))
            {
                ShowTrayNotification("原位替换", "未取到选中文本，无法原位替换", NotificationIcon.Warning);
                return;
            }

            var text = PrepareCapturedText(captured, out _);
            var settings = _services!.GetRequiredService<AppSettings>();
            var translation = await _services!.GetRequiredService<InPlaceTranslationService>().TranslateAsync(
                text, settings.SourceLanguage, settings.TargetLanguage);
            var replacement = await _services!.GetRequiredService<SelectionReplacementService>().ReplaceAsync(
                targetWindow, translation.Text);
            if (!replacement.Success)
            {
                ShowTrayNotification("原位替换", replacement.Message, NotificationIcon.Warning);
                _services!.GetRequiredService<QuickWindow>().ShowForSelection(text);
                SetActionableIssue("原位替换", replacement.Message, "诊断");
            }
            else
            {
                _replacementToast ??= new ReplacementToastWindow();
                _replacementToast.ShowMessage("已替换，Ctrl+Z 可撤销");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "翻译并替换选区失败");
            ShowTrayNotification("原位替换", "翻译并替换失败，已取消本次操作", NotificationIcon.Warning);
            SetActionableIssue("原位替换", "翻译或替换失败，已取消本次操作", "诊断");
        }
        finally
        {
            _replacementInProgress = false;
        }
    }

    /// <summary>
    /// 托盘创建允许失败：清理半成品对象并让应用降级为可见的设置窗口模式，
    /// 避免 Shell 托盘瞬时故障把进程变成既无窗口也无托盘的不可操作状态。
    /// </summary>
    private bool TryInitTray()
    {
        try
        {
            InitTray();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "托盘图标创建失败，改用设置窗口模式继续启动");
            try
            {
                _trayIcon?.Dispose();
            }
            catch (Exception cleanupEx)
            {
                Log.Warning(cleanupEx, "清理失败的托盘对象时出错");
            }
            _trayIcon = null;
            return false;
        }
    }

    private void InitTray()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "译印 INKSEAL",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
        };

        // 右键菜单（FR-008）：输入翻译 / 划词翻译 / 设置 / 退出
        var inputItem = new MenuItem { Header = "输入翻译" };
        inputItem.Click += (_, _) => ShowQuickInput();

        var selectItem = new MenuItem { Header = "划词翻译" };
        selectItem.Click += (_, _) => _ = TranslateSelectionAsync();

        // FR-021：截图翻译（与热键同一入口，热键被占用时仍可从托盘触发）
        var replaceItem = new MenuItem { Header = "翻译并替换选中文本" };
        replaceItem.Click += (_, _) => _ = TranslateAndReplaceSelectionAsync();
        var captureItem = new MenuItem { Header = "截图翻译" };
        captureItem.Click += (_, _) => _ = _services!.GetRequiredService<ScreenCaptureTranslateFlow>().StartAsync();

        // FR-027：一键清理桌面（存在钉图时才可用，菜单展开时刷新状态）
        var closePinsItem = new MenuItem { Header = "关闭所有钉图" };
        closePinsItem.Click += (_, _) => _services?.GetService<PinWindowManager>()?.CloseAll();

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => ShowSettings();

        // FR-037（批 2）：场景模式子菜单（标准/内置/自定义，展开时重建勾选态）
        var profileMenu = new MenuItem { Header = "场景模式" };
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
            ShowTrayNotification("隐私模式",
                on ? "隐私模式已开启：不再写入历史与日志" : "隐私模式已关闭",
                NotificationIcon.Info);
        };

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();

        // 运行状态与修复：用户主动打开时才刷新，平时不打扰、不反复弹泡。
        var statusMenu = new MenuItem { Header = "运行状态与修复" };
        statusMenu.SubmenuOpened += (_, _) => RebuildStatusMenu(statusMenu);
        statusMenu.Items.Add(new MenuItem { Header = "正在读取…" });

        var menu = new ContextMenu();
        menu.Items.Add(inputItem);
        menu.Items.Add(selectItem);
        menu.Items.Add(captureItem);
        menu.Items.Add(replaceItem);
        menu.Items.Add(closePinsItem);
        menu.Items.Add(profileMenu);
        menu.Items.Add(statusMenu);
        menu.Items.Add(settingsItem);
        menu.Items.Add(privacyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        menu.Opened += (_, _) =>
        {
            closePinsItem.IsEnabled = _services?.GetService<PinWindowManager>()?.HasPins == true;
            privacyItem.IsChecked = appSettings.PrivacyMode; // 设置页可能改过，展开时同步
            replaceItem.IsEnabled = appSettings.ReplaceSelectionEnabled;
        };
        _trayIcon.ContextMenu = menu;

        // 左键双击托盘图标打开设置主窗口（FR-008）
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowSettings();

        _trayIcon.ForceCreate(); // 代码构建（不在 XAML 可视树中）需显式创建
    }

    /// <summary>
    /// 右下角通知：走译印自家的「印讯」卡（系统气泡还带着旧版观感，已经不用了）。
    /// 印讯必须在 UI 线程建窗，所以统一 Post；建窗失败这种极端情况再退回托盘气泡，消息不丢。
    /// </summary>
    private void ShowTrayNotification(string title, string message, NotificationIcon icon)
    {
        var level = icon switch
        {
            NotificationIcon.Error => SealToastLevel.Error,
            NotificationIcon.Warning => SealToastLevel.Warning,
            _ => SealToastLevel.Info,
        };

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _trayIcon?.ShowNotification(title, message, icon);
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                SealToastWindow.ShowToast(title, message, level);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "印讯窗口创建失败，退回托盘气泡");
                _trayIcon?.ShowNotification(title, message, icon);
            }
        });
    }

    private void ShowStartBalloonIfEnabled()
    {
        var settings = _services!.GetRequiredService<AppSettings>();
        if (settings.ShowStartBalloon)
        {
            ShowTrayNotification("启动就绪", BuildStartBalloonText(settings), NotificationIcon.Info);
        }
    }

    /// <summary>
    /// 启动气泡文案：三个热键均取自当前设置（解析失败回退默认值），
    /// 避免写死文案后用户改了热键、气泡仍显示旧键（FR-001/021）。
    /// </summary>
    internal static string BuildStartBalloonText(AppSettings settings) =>
        $"{HotkeyText(settings.HotkeyInputTranslate, HotkeyDefinition.DefaultInput)} 输入翻译 · "
        + $"{HotkeyText(settings.HotkeySelectTranslate, HotkeyDefinition.DefaultSelect)} 划词翻译 · "
        + $"{HotkeyText(settings.HotkeyCaptureTranslate, HotkeyDefinition.DefaultCapture)} 截图翻译"
        + ThemeAutoSuffix(settings);

    /// <summary>
    /// 按时间档时补一句此刻为何是纸/墨、下次何时翻转——否则用户晚上开机会疑惑「我什么时候选的黑色」。
    /// 手动档（纸/墨/跟随系统）不补：那是用户自己选的，无需解释。
    /// </summary>
    private static string ThemeAutoSuffix(AppSettings settings)
    {
        if (!string.Equals(settings.Theme, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var (isPaper, flipHour) = ThemeManager.ResolvePaperWindow(
            settings.ThemePaperFromHour, settings.ThemePaperToHour, DateTime.Now);
        return $" · 此刻按时间 → {(isPaper ? "纸" : "墨")}，{flipHour:00}:00 转{(isPaper ? "墨" : "纸")}";
    }

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
                quickWindow.ShowForSelection(PrepareCapturedText(text, out var cleaned), cleaned: cleaned,
                    origin: QuickWindow.OriginClipboard);
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
        try
        {
            var window = _services!.GetRequiredService<MainWindow>();
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开设置窗口失败");
            ShowTrayNotification("打开设置失败", "详见日志", NotificationIcon.Warning);
        }
    }

    /// <summary>记录一个可处理的问题（托盘操作中心与诊断页共用同一份事实）。</summary>
    private void SetActionableIssue(string area, string detail, string section)
    {
        _lastActionableIssueArea = area;
        _lastActionableIssueDetail = detail;
        _lastActionableIssueSection = section;
        Log.Information("记录可处理问题：{Area} → {Section}", area, section);
    }

    /// <summary>托盘操作中心的例行状态：引擎、档案、隐私与热键注册情况。</summary>
    private (string Engine, string Profile, string Privacy, string Hotkeys) ReadOperationalSnapshot()
    {
        if (_services is null)
        {
            return ("服务未就绪", "-", "-", "-");
        }

        try
        {
            var settings = _services.GetRequiredService<AppSettings>();
            var catalog = _services.GetRequiredService<TranslatorCatalog>();
            var profiles = _services.GetRequiredService<ProfileService>();
            var engine = catalog.Resolve(settings.Engine);
            var hotkeys = _hotkeyManager;
            return (
                $"{engine.Name}{(engine.IsConfigured ? "" : "（未配置）")}",
                profiles.DisplayName,
                settings.PrivacyMode ? "开启" : "关闭",
                DescribeHotkeyState(hotkeys, settings));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取运行状态失败");
            return ("读取失败", "-", "-", "-");
        }
    }

    private static string DescribeHotkeyState(HotkeyManager? hotkeys, AppSettings settings)
    {
        if (hotkeys is null)
        {
            return "未初始化";
        }

        var expected = new[]
        {
            "input",
            "select",
            "capture",
            "profile",
        };
        var missing = expected.Where(name => !hotkeys.IsRegistered(name)).ToArray();
        if (missing.Length == 0)
        {
            return "核心热键均已注册";
        }

        return $"{missing.Length} 个未注册";
    }

    /// <summary>
    /// 首次运行上手卡（FR-059）：弹之前先把“看过”落盘——用户直接关掉进程也不该下次再被弹一次。
    /// 首启「启印」：开主窗体（默认落在工作台），在整窗上铺一层启封动画——
    /// 印砸下、INKSEAL 打完、光透出来，播完收层。
    /// 收层后再把控制交回 <paramref name="then"/>，避免和上手卡挤在同一帧。
    /// 系统关了客户端动画、或窗体起不来时直接跳过，不打扰。
    /// </summary>
    private void ScheduleLaunchReveal(Action? then)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var window = _services!.GetRequiredService<MainWindow>();
                // 先铺层再 Show()：窗口一可见，合成线程就会抓走一帧；那一刻启印层若还是
                // Collapsed，用户先看到的就是工作台——「工作台闪一下才播动画」就是这么来的。
                var primed = window.PrimeLaunchReveal();
                window.Show();
                window.Activate();

                if (!primed)
                {
                    then?.Invoke();
                    return;
                }

                window.PlayLaunchReveal();

                void OnFinished(object? sender, EventArgs args)
                {
                    window.RevealFinished -= OnFinished;
                    then?.Invoke();
                }

                window.RevealFinished += OnFinished;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "启印动画显示失败（忽略）");
                then?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 设置页「再看一次」走的是另一条路径（不改动这个标记）。
    /// </summary>
    private void ScheduleOnboardingCard(AppSettings settings, ISettingsStore store)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            settings.OnboardingShown = true;
            EnableAutoStartOnFirstRun();
            store.Save(settings);
            var services = _services;
            if (services is null)
            {
                return;
            }
            try
            {
                var guide = CreateGuide(services, settings, store);
                guide.Show();
                guide.Activate();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "上手卡显示失败（忽略）");
            }
        };
        timer.Start();
    }

    /// <summary>建一张「立契」并接好两条回宿主的线（诊断 / 指定设置分区）。</summary>
    private FirstRunGuideWindow CreateGuide(IServiceProvider services, AppSettings settings, ISettingsStore store)
    {
        var guide = new FirstRunGuideWindow(
            settings,
            store,
            services.GetRequiredService<HotkeyManager>(),
            services.GetRequiredService<TranslatorCatalog>(),
            services.GetRequiredService<OcrService>(),
            services.GetRequiredService<InPlaceTranslationService>(),
            services.GetRequiredService<AutoStart>());
        guide.OpenDoctorRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowSettings();
            services.GetRequiredService<MainWindow>().NavigateToSection("诊断");
        });
        guide.OpenSettingsRequested += (_, section) => Dispatcher.BeginInvoke(() =>
        {
            ShowSettings();
            services.GetRequiredService<MainWindow>().NavigateToSection(section);
        });
        return guide;
    }

    /// <summary>
    /// <c>--onboarding</c>：把「立契」直接摆出来给用户复核。只上屏，不写任何设置——
    /// 不会把「看过」标记改回去，也不会顺手把开机自启打开。
    /// </summary>
    private void ShowOnboardingPreview()
    {
        var services = _services;
        if (services is null)
        {
            return;
        }

        try
        {
            var guide = CreateGuide(
                services,
                services.GetRequiredService<AppSettings>(),
                services.GetRequiredService<ISettingsStore>());
            guide.Show();
            guide.Activate();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "预览立契窗口失败（忽略）");
        }
    }
    /// <summary>
    /// 首次运行把开机自启打开一次：译印的承诺是「Alt+D 随时呼出」，重启后不启动等于承诺破产。
    /// 只在首启（上手卡未看过）默认一次，之后完全听用户的——设置页开关与托盘菜单都能改，不再自动改回来。
    /// </summary>
    private void EnableAutoStartOnFirstRun()
    {
        try
        {
            _services?.GetRequiredService<AutoStart>().SetEnabled(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "首次运行开启开机自启失败");
        }
    }

    /// <summary>读取 <c>--name value</c> 形式的命令行参数值（缺失或为空时返回 false）。</summary>
    private static bool TryGetArgValue(string[] args, string name, out string value)
    {
        value = "";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                value = args[i + 1];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 开发/QA 用的离屏渲染：纸/墨两套主题各出一轮，把设置主窗与小窗经
    /// Measure/Arrange + RenderTargetBitmap 落成 PNG。**不显示任何窗口**，
    /// 因此不抢前台焦点、不占鼠标，可在无人值守环境核对视觉稿。
    /// </summary>
    private void RenderUiScreens(string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            RenderThemeScreens(AppTheme.Light, "paper", outputDir);
            RenderThemeScreens(AppTheme.Dark, "ink", outputDir);
            Log.Information("离屏渲染完成：{Dir}", outputDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "离屏渲染失败");
        }
        finally
        {
            Shutdown(0);
        }
    }

    /// <summary>按一套主题新建窗口并出图（每套主题用全新窗口，避免 DynamicResource 复用旧值）。</summary>
    private void RenderThemeScreens(AppTheme theme, string suffix, string outputDir)
    {
        ThemeManager.Apply(theme);
        var services = _services!;

        // 小窗 VM 是单例，会带着上一轮主题写入的演示文本，先清空再让工作台出图
        var vm = services.GetRequiredService<QuickTranslateViewModel>();
        vm.InputText = "";
        vm.ResultText = "";

        var settingsVm = services.GetRequiredService<SettingsViewModel>();
        settingsVm.ShowAdvancedSettings = true; // 高级分区也要进图，否则渲染不到它们的真实版面
        // 自检清单是「跑完才有」的版面，出图前注入一组样例，否则诊断页永远只能拍到空态
        settingsVm.SeedDoctorResultsForRender(DoctorRenderSample());
        // 藏印页同理：样例词条能让「存词行」的真实版面上图（不落库）
        settingsVm.SeedVocabularyForRender();
        // 定制印页同理：样例词条让「词条」区的真实行版面上图（不落库）
        settingsVm.SeedGlossaryForRender();
        // 工作台再出一张「已落印」的图：空态看不出 25px 译文、印文行与右上印面标到底长什么样
        vm.InputText = "We were caught off guard by how good it turned out.";
        vm.ResultText = "我们没料到结果会这么好。";
        vm.LastEngineName = "Bing";
        vm.ResultSubText = "定制印 0 条命中，未走印谱缓存。";
        vm.LastElapsedMs = 118;
        var main = new MainWindow(settingsVm, services.GetRequiredService<HotkeyManager>());
        var mainRoot = (FrameworkElement)main.Content;
        var index = 0;
        foreach (var tab in main.NavTabs.Items.OfType<TabItem>())
        {
            main.NavTabs.SelectedItem = tab;
            DrainDispatcher(); // 工作台/印谱的数据是 async 查库后落定的，不推消息队列就只拍到空态
            var name = tab.Header as string ?? "section";
            RenderElement(mainRoot, main.Width, main.Height,
                Path.Combine(outputDir, $"main-{index:00}-{name}-{suffix}.png"));

            // 长页面（通用 / 翻译 / 高级…）一屏拍不下下半截，滚到底再拍一张给 QA 核对
            if (tab.Content is DependencyObject content
                && FindScrollViewer(content) is { ScrollableHeight: > 1 } scroller)
            {
                scroller.ScrollToEnd();
                DrainDispatcher();
                RenderElement(mainRoot, main.Width, main.Height,
                    Path.Combine(outputDir, $"main-{index:00}-{name}-{suffix}-b.png"));
                scroller.ScrollToTop();
                DrainDispatcher();
            }

            index++;
        }

        // 「启印」层叠在工作台上的样子（末帧）：真机上它是整窗盖一层，离屏只能这样摆出来，
        // 用来核对层叠、尺寸与遮挡范围（动画过程本身拍不到）。
        main.ShowLaunchRevealFinalFrame();
        DrainDispatcher();
        RenderElement(mainRoot, main.Width, main.Height,
            Path.Combine(outputDir, $"main-launch-reveal-{suffix}.png"));

        // 模型下拉的行（行尾 × / 选中勾 / 落点插入线）：真机上弹层是独立 HWND，窗口截图拍不到，
        // 这里用同一条 ItemContainerStyle 摆几行出来，核对行高、× 与勾的位置、插入线落在哪。
        var engineTab = main.NavTabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string) == "引擎");
        if (engineTab is not null)
        {
            main.NavTabs.SelectedItem = engineTab;
            DrainDispatcher();
            if (FindDescendant<ComboBox>(mainRoot, c => c.DataContext is AiProviderCardViewModel) is { } modelCombo)
            {
                var rows = new StackPanel();
                rows.Children.Add(ModelDropRow(modelCombo, "oc/mimo-v2.6-flash-free", selected: true, chip: true, line: null));
                rows.Children.Add(ModelDropRow(modelCombo, "oc/mimo-v2.5-free", selected: false, chip: true, line: "DropAfter"));
                rows.Children.Add(ModelDropRow(modelCombo, "gpt-4o-mini（接口拉回）", selected: false, chip: false, line: null));
                var shell = new Border { Child = rows, Padding = new Thickness(0, 4, 0, 4) };
                shell.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
                shell.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
                shell.SetResourceReference(Border.CornerRadiusProperty, "Radius.Card");
                shell.BorderThickness = new Thickness(1);
                shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                RenderElement(shell, 400, Math.Ceiling(shell.DesiredSize.Height),
                    Path.Combine(outputDir, $"model-drop-rows-{suffix}.png"));
            }
        }



        // 小窗要从真空态出图，先把工作台那张图的演示文本收回
        vm.InputText = "";
        vm.ResultText = "";
        vm.LastEngineName = "";
        vm.ResultSubText = "";
        vm.LastElapsedMs = 0;

        var quick = new QuickWindow(vm, services.GetRequiredService<AppSettings>(),
            services.GetRequiredService<ISettingsStore>());
        RenderElement((FrameworkElement)quick.Content, quick.Width, quick.Height,
            Path.Combine(outputDir, $"quick-empty-{suffix}.png"));

        vm.InputText = "Ink and seal, the two halves of a promise.";
        vm.ResultText = "墨与印，是一句承诺的两半。";
        RenderElement((FrameworkElement)quick.Content, quick.Width, quick.Height,
            Path.Combine(outputDir, $"quick-result-{suffix}.png"));


        // 悬停取词印标：真机上只在拖选后跟光标出现，没有窗口可拍。离屏直接把它摆出来，
        // 而且放大四倍出图（Viewbox 是矢量的，放大后还是清楚的），好核对印面、刻痕与纸底。
        var badge = new HoverBadgeWindow();
        RenderElement((FrameworkElement)badge.Content, 160, 160,
            Path.Combine(outputDir, $"hover-badge-{suffix}.png"));

        // 首启「立契」：窗口自带 WindowChrome，不能直接渲窗口底（会是透明的），把 Content 脱下来
        // 塞进一层带窗口底的宿主。先出「启封」静止帧，再出「契成」静止帧——
        // 前一帧 Arrange 过后进度条的 ActualWidth 才有值，末帧的进度条才是满的。
        var guide = new FirstRunGuideWindow(
            services.GetRequiredService<AppSettings>(),
            services.GetRequiredService<ISettingsStore>(),
            services.GetRequiredService<HotkeyManager>(),
            services.GetRequiredService<TranslatorCatalog>(),
            services.GetRequiredService<OcrService>(),
            services.GetRequiredService<InPlaceTranslationService>(),
            services.GetRequiredService<AutoStart>());
        var guideRoot = (FrameworkElement)guide.Content;
        guide.Content = null;
        var guideHost = new Border { Child = guideRoot, CornerRadius = new CornerRadius(14) };
        guideHost.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        for (var step = 0; step <= 3; step++)
        {
            var name = step == 0 ? "first-run-guide" : $"first-run-guide-step{step}";
            var path = Path.Combine(outputDir, $"{name}-{suffix}.png");
            guide.ShowStepFrame(step);
            RenderElement(guideHost, guide.Width, guide.Height, path);
            if (step == 3)
            {
                // 盖印页的进度条要等排版过一次才有宽度，第二次才是满格的真图。
                guide.ShowStepFrame(step);
                RenderElement(guideHost, guide.Width, guide.Height, path);
            }
        }

        guide.ShowFinalFrame();
        RenderElement(guideHost, guide.Width, guide.Height,
            Path.Combine(outputDir, $"first-run-guide-final-{suffix}.png"));
        guide.Close();

        // 首启「启印」：窗口 520×392，无边框透明底，只出末帧（印已落、INKSEAL 已打完）。
        // 首启「启印」：铺在主窗体上的一层，按整窗尺寸出末帧（印已落、INKSEAL 已打完、光已透）。
        var launch = new LaunchRevealView();
        launch.ShowFinalFrame();
        RenderElement(launch, 1140, 780, Path.Combine(outputDir, $"launch-reveal-{suffix}.png"));

        // 飞行姿态（整段动画里最高的那一瞬）：核对印面有没有被窗口上沿切掉。
        launch = new LaunchRevealView();
        launch.ShowFlightPose();
        RenderElement(launch, 1140, 780, Path.Combine(outputDir, $"launch-flight-{suffix}.png"));
    }

    /// <summary>
    /// 离屏渲染用的诊断样例：与设计稿 03 诊断的八行一一对应（其中两行带可修建议），
    /// 只用于 QA 出图核对版面，不进入真实诊断路径。
    /// </summary>
    private static IReadOnlyList<DiagnosticResult> DoctorRenderSample() =>
    [
        new("hotkeys", "全局热键注册", DiagnosticStatus.Passed, "Alt+S · Alt+D · Alt+O"),
        new("ocr-windows", "Windows OCR 语言包", DiagnosticStatus.Passed, "zh-Hans / en"),
        new("ocr-paddle", "本地高精度 OCR（Paddle）", DiagnosticStatus.Passed, "22.5 MB · 内置"),
        new("proxy", "网络代理连通性", DiagnosticStatus.Warning, "127.0.0.1:7897 · 仅国外引擎",
            "点一下就在当前网络下实际探一次 Google / Azure / DeepL。"),
        new("database", "数据库完整性", DiagnosticStatus.Passed, "wal · 4.1 MB"),
        new("data-directory", "数据目录可写", DiagnosticStatus.Passed, "可写"),
        new("update-channel", "更新清单验签", DiagnosticStatus.Passed, "ed25519"),
        new("update-replace", "EXE 替换权限", DiagnosticStatus.Failed, "安装在 Program Files",
            "把 EXE 移到用户目录，或改用便携模式。"),
    ];

    /// <summary>把一个元素树按指定 DIP 尺寸排版后渲染成 PNG（不接入 PresentationSource）。</summary>
    /// <summary>
    /// 出图前把 Dispatcher 队列推空：渲染线程本身不跑消息循环，但视图模型里「今日印记」「最近落印」
    /// 这些数据是异步查库后落定的，不推队列只能拍到空态。只推本地队列，不触发任何网络请求。
    /// </summary>
    private static void DrainDispatcher(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(30);
        }
    }

    /// <summary>在可视树里找第一个 ScrollViewer：外层页面滚动器比列表内层先命中。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>摆一行模型下拉项：用真身上那条 ItemContainerStyle，选中 / × / 插入线按参数点亮。</summary>
    private static ComboBoxItem ModelDropRow(ComboBox combo, string name, bool selected, bool chip, string? line)
    {
        var row = new ComboBoxItem { Content = name, Style = combo.ItemContainerStyle, IsSelected = selected };
        row.ApplyTemplate();
        if (chip && row.Template?.FindName("RowDelete", row) is Border delete)
        {
            delete.Visibility = Visibility.Visible;
        }

        if (line is not null && row.Template?.FindName(line, row) is Border drop)
        {
            drop.Visibility = Visibility.Visible;
        }

        return row;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && match(hit))
            {
                return hit;
            }

            if (FindDescendant(child, match) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RenderElement(FrameworkElement root, double width, double height, string path)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
