using System.Reflection;
using System.IO;
using System.Net.Http;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Diagnostics;
using TranslationApp.Core.Hotkey;
using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using TranslationApp.Core.Updates;

namespace TranslationApp.Services;

/// <summary>
/// Doctor 诊断中心：集中探测热键、OCR、代理、SQLite/本地写入与签名更新。
/// 检查不修改设置，不临时注销热键；联网项只在用户显式点击“开始诊断”后执行。
/// </summary>
public sealed class DoctorService
{
    private static readonly string ProbeGoogleEndpoint =
        "https://translate.googleapis.com/translate_a/single"
        + "?client=dict-chrome-ex&dt=t&sl=auto&tl=zh-CN&q=hello";

    private readonly AppSettings _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly OcrService _ocr;
    private readonly HistoryDatabase _database;
    private readonly UpdateService _updates;
    private readonly OcrWorkerLauncher? _workerLauncher;

    public DoctorService(
        AppSettings settings,
        HotkeyManager hotkeys,
        OcrService ocr,
        HistoryDatabase database,
        UpdateService updates,
        OcrWorkerLauncher? workerLauncher = null)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _ocr = ocr;
        _database = database;
        _updates = updates;
        _workerLauncher = workerLauncher;
    }

    public async Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticResult>();
        // 热键状态必须在首次 await 前读取：HotkeyManager 与设置窗口同线程创建。
        results.AddRange(CheckHotkeys());
        results.Add(CheckWindowsOcr());
        results.Add(CheckPaddleOcr());
        results.Add(await CheckOcrWorkerAsync(cancellationToken));
        results.Add(CheckDatabase());
        results.Add(CheckDataDirectory());
        results.AddRange(CheckUpdateInstallability());

        try
        {
            results.Add(await CheckProxyAsync(cancellationToken));
            results.Add(await CheckUpdateChannelAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        return new DiagnosticReport(
            DateTimeOffset.UtcNow,
            CurrentVersion().ToString(),
            AppPaths.IsPortable,
            results);
    }

    private IEnumerable<DiagnosticResult> CheckHotkeys()
    {
        var slots = new[]
        {
            new HotkeySlot("hotkey-input", "hotkey:输入翻译", "input",
                _settings.HotkeyInputTranslate, true),
            new HotkeySlot("hotkey-select", "hotkey:划词翻译", "select",
                _settings.HotkeySelectTranslate, true),
            new HotkeySlot("hotkey-capture", "hotkey:截图翻译", "capture",
                _settings.HotkeyCaptureTranslate, true),
            new HotkeySlot("hotkey-profile", "hotkey:场景档案", "profile",
                _settings.HotkeySwitchProfile, true),
            new HotkeySlot("hotkey-replace", "hotkey:翻译并替换", "replace",
                _settings.HotkeyReplaceTranslate, _settings.ReplaceSelectionEnabled),
        };

        var parsedBySlot = new Dictionary<string, HotkeyDefinition>();
        foreach (var slot in slots)
        {
            if (HotkeyDefinition.TryParse(slot.Value, out var definition))
            {
                parsedBySlot[slot.Name] = definition;
            }
        }

        var duplicateKeys = parsedBySlot
            .GroupBy(pair => pair.Value, pair => pair.Key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        foreach (var slot in slots)
        {
            if (!slot.Enabled)
            {
                yield return new DiagnosticResult(
                    slot.Id,
                    $"全局热键 · {slot.Label}",
                    DiagnosticStatus.Skipped,
                    $"当前组合为 {DisplayHotkey(slot.Value, DefaultFor(slot.Name))}，功能未启用，热键未占用系统注册。",
                    "需要原位替换时，在热键页启用后再检查占用。");
                continue;
            }

            if (!HotkeyDefinition.TryParse(slot.Value, out var definition))
            {
                var fallback = DefaultFor(slot.Name);
                var fallbackText = fallback.ToString();
                yield return new DiagnosticResult(
                    slot.Id,
                    $"全局热键 · {slot.Label}",
                    _hotkeys.IsRegistered(slot.Name) ? DiagnosticStatus.Warning : DiagnosticStatus.Failed,
                    $"设置值“{Safe(slot.Value)}”格式无效；"
                    + (_hotkeys.IsRegistered(slot.Name)
                        ? $"启动时已回退为 {fallbackText} 并成功注册。"
                        : $"未找到有效的运行时注册（预期回退为 {fallbackText}）。"),
                    "在热键页重新录入有效组合：修饰键 + 字母、数字或 F1~F12。");
                continue;
            }

            if (duplicateKeys.Contains(definition))
            {
                yield return new DiagnosticResult(
                    slot.Id,
                    $"全局热键 · {slot.Label}",
                    DiagnosticStatus.Failed,
                    $"{definition} 同时被多个功能配置，只能有一个功能获得系统热键。",
                    "在热键页为每个功能分配不同组合。");
                continue;
            }

            yield return _hotkeys.IsRegistered(slot.Name)
                ? new DiagnosticResult(
                    slot.Id,
                    $"全局热键 · {slot.Label}",
                    DiagnosticStatus.Passed,
                    $"{definition} 已由本程序成功注册，当前未发现运行时冲突。",
                    null)
                : new DiagnosticResult(
                    slot.Id,
                    $"全局热键 · {slot.Label}",
                    DiagnosticStatus.Failed,
                    $"{definition} 未注册，可能被其他程序占用，或启动时注册失败。",
                    "更换一个组合，或关闭占用该组合的程序后重启速译。");
        }
    }

    private DiagnosticResult CheckWindowsOcr()
    {
        if (!_ocr.IsAvailable)
        {
            return new DiagnosticResult(
                "ocr-windows",
                "Windows OCR 语言包",
                DiagnosticStatus.Failed,
                OcrLanguages.MissingPackMessage,
                "安装需要的 OCR 语言包后重启速译。");
        }

        var status = _ocr.ResolveStatus(_settings.OcrLanguage);
        if (status.IsFallback)
        {
            return new DiagnosticResult(
                "ocr-windows",
                "Windows OCR 语言包",
                DiagnosticStatus.Warning,
                $"指定语言“{Safe(_settings.OcrLanguage)}”缺失，已回退到系统首选语言；"
                + $"当前可用 {status.Available.Count} 个语言包。",
                "在 Windows 语言设置中补充该语言的“光学字符识别”可选功能。");
        }

        var languages = string.Join(
            "、",
            status.Available.Take(4)
                .Select(language => language.DisplayName)
                .Concat(status.Available.Count > 4 ? [" 等"] : []));
        return new DiagnosticResult(
            "ocr-windows",
            "Windows OCR 语言包",
            DiagnosticStatus.Passed,
            $"可用语言包 {status.Available.Count} 个（{languages}），当前设置可解析。",
            null);
    }

    private DiagnosticResult CheckPaddleOcr()
    {
        var missing = _ocr.PaddleModelResources.Where(resource =>
        {
            using var stream = typeof(DoctorService).Assembly.GetManifestResourceStream(resource);
            return stream is null;
        }).ToArray();

        if (missing.Length > 0)
        {
            return new DiagnosticResult(
                "ocr-paddle",
                "PaddleOCR 嵌入模型",
                DiagnosticStatus.Failed,
                $"缺少 {missing.Length}/{_ocr.PaddleModelResources.Count} 个模型资源。",
                "使用完整发布产物重新安装；当前若选择 PaddleOCR 会自动回退到 Windows OCR。");
        }

        if (_ocr.IsPaddleDegraded)
        {
            return new DiagnosticResult(
                "ocr-paddle",
                "PaddleOCR 嵌入模型",
                DiagnosticStatus.Warning,
                "4/4 模型资源完整，但 OCR 隔离进程曾启动或识别失败，已回退到 Windows OCR。",
                "检查杀毒软件隔离记录与临时目录权限；重启速译后可再试。");
        }

        var selected = string.Equals(
            OcrEngineNames.Normalize(_settings.OcrLocalEngine),
            OcrEngineNames.Paddle,
            StringComparison.Ordinal);
        return selected
            ? new DiagnosticResult(
                "ocr-paddle",
                "PaddleOCR 嵌入模型",
                DiagnosticStatus.Passed,
                "4/4 模型资源完整；本次检查不预建推理会话，首次识别时验证 ONNX 运行时。",
                null)
            : new DiagnosticResult(
                "ocr-paddle",
                "PaddleOCR 嵌入模型",
                DiagnosticStatus.Skipped,
                "当前使用 Windows OCR；4/4 嵌入模型资源已打包，未执行在线下载或推理。",
                null);
    }

    /// <summary>
    /// P0「OCR 原生引擎进程隔离」：真的把同 EXE 以 <c>--ocr-worker</c> 拉起来并走一次握手，
    /// 确认「起得来、说得上话」。只握手不建推理会话（不加载 ONNX 模型，秒级返回、不占内存）。
    /// </summary>
    private async Task<DiagnosticResult> CheckOcrWorkerAsync(CancellationToken cancellationToken)
    {
        const string id = "ocr-worker";
        const string label = "OCR 隔离进程";

        if (_workerLauncher is null)
        {
            return new DiagnosticResult(
                id, label, DiagnosticStatus.Warning,
                "未装配隔离进程启动器，PaddleOCR 选项不可用（恒走系统识别）。",
                "使用完整发布产物（EXE/apphost）运行；以 dotnet 直接托管 dll 时不支持自启动子进程。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var channel = await _workerLauncher.OpenAsync(timeout.Token);
            var hello = await OcrWorkerProtocol.HandshakeAsync(channel, timeout.Token);
            if (!hello.Ok || !string.Equals(hello.Kind, OcrWorkerProtocol.KindHello, StringComparison.Ordinal))
            {
                return new DiagnosticResult(
                    id, label, DiagnosticStatus.Failed,
                    $"隔离进程已连接，但握手应答异常：{Safe(hello.Message ?? hello.Kind)}",
                    "查看 ocr-worker-*.log 与杀毒软件隔离记录。");
            }

            return new DiagnosticResult(
                id, label, DiagnosticStatus.Passed,
                $"隔离进程已应答（PID {hello.Pid}，版本 {Safe(hello.Version)}）；"
                + "PaddleOCR 的原生 ONNX 库即使崩溃也只影响该子进程，主进程与热键不受影响。",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticResult(
                id, label, DiagnosticStatus.Failed,
                "隔离进程探测超过 10 秒未应答。",
                "检查 EXE 是否被安全软件拦截，以及 %TEMP% 是否可写（模型落盘在该目录）。");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                id, label, DiagnosticStatus.Failed,
                $"隔离进程启动或握手失败：{Safe(ex.Message)}",
                "确认使用发布版 EXE 运行，并检查 EXE 同目录权限与安全软件拦截记录。");
        }
    }

    private DiagnosticResult CheckDatabase()
    {
        if (!_database.IsAvailable)
        {
            return new DiagnosticResult(
                "database",
                "历史数据库",
                DiagnosticStatus.Failed,
                Safe(_database.UnavailableReason ?? "数据库初始化失败，功能已降级关闭。"),
                "检查数据目录权限与磁盘空间；必要时导出旧备份后重建数据库。");
        }

        try
        {
            using var connection = _database.OpenConnection();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            var integrity = check.ExecuteScalar()?.ToString() ?? "";
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new DiagnosticResult(
                    "database",
                    "历史数据库",
                    DiagnosticStatus.Failed,
                    $"SQLite quick_check 返回“{Safe(integrity)}”。",
                    "从最近的正常备份恢复，避免继续写入受损数据库。");
            }

            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM History;";
            var historyCount = Convert.ToInt64(count.ExecuteScalar() ?? 0L);
            return new DiagnosticResult(
                "database",
                "历史数据库",
                DiagnosticStatus.Passed,
                $"SQLite quick_check=ok，连接可读；现有历史记录 {historyCount} 条。",
                null);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                "database",
                "历史数据库",
                DiagnosticStatus.Failed,
                $"数据库读取检查失败：{Safe(ex.Message)}",
                "检查数据目录权限、磁盘空间与杀毒软件锁定。");
        }
    }

    private DiagnosticResult CheckDataDirectory()
    {
        var probePath = Path.Combine(AppPaths.DataDirectory, $".doctor-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return new DiagnosticResult(
                "data-directory",
                "本地数据目录",
                DiagnosticStatus.Passed,
                $"数据目录可创建、写入和删除（便携模式：{(AppPaths.IsPortable ? "是" : "否")}）。",
                null);
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                "data-directory",
                "本地数据目录",
                DiagnosticStatus.Failed,
                $"写入测试失败：{Safe(ex.Message)}",
                "检查目录权限与磁盘空间；便携模式还需确认安装目录可写。");
        }
        finally
        {
            try
            {
                if (File.Exists(probePath)) File.Delete(probePath);
            }
            catch
            {
                // 清理失败不覆盖原始诊断结论。
            }
        }
    }

    private async Task<DiagnosticResult> CheckProxyAsync(CancellationToken cancellationToken)
    {
        if (!_settings.ProxyEnabled)
        {
            return new DiagnosticResult(
                "proxy",
                "代理连通性",
                DiagnosticStatus.Skipped,
                "代理未启用，本次按直连路径跳过网络探测。",
                "使用国外引擎前可在高级页启用代理并再次诊断。");
        }

        if (string.IsNullOrWhiteSpace(_settings.ProxyHost) || _settings.ProxyPort is <= 0 or > 65535)
        {
            return new DiagnosticResult(
                "proxy",
                "代理连通性",
                DiagnosticStatus.Failed,
                $"代理地址或端口无效（端口 {Safe(_settings.ProxyPort.ToString())}）。",
                "填写有效代理主机与 1~65535 的端口。");
        }

        var scheme = string.Equals(_settings.ProxyScheme, "socks5", StringComparison.OrdinalIgnoreCase)
            ? "socks5"
            : "http";
        var endpoint = $"{scheme.ToUpperInvariant()} 代理 {Safe(_settings.ProxyHost)}:{_settings.ProxyPort}";
        if (!string.Equals(_settings.ProxyScheme, scheme, StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticResult(
                "proxy",
                "代理连通性",
                DiagnosticStatus.Failed,
                $"{endpoint} 使用了不受支持的协议“{Safe(_settings.ProxyScheme)}”。",
                "在高级页选择 HTTP 或 SOCKS5。");
        }

        var password = SecretStore.Unprotect(_settings.ProxyPasswordEncrypted) ?? "";
        var options = new ProxyOptions(
            _settings.ProxyHost.Trim(),
            _settings.ProxyPort,
            _settings.ProxyUserName?.Trim(),
            password,
            scheme);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            using var client = TranslationHttpClientFactory.Create(
                options.CreateWebProxy(), TimeSpan.FromSeconds(12));
            using var response = await client.GetAsync(
                ProbeGoogleEndpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var statusCode = (int)response.StatusCode;
            return statusCode switch
            {
                200 => new DiagnosticResult(
                    "proxy", "代理连通性", DiagnosticStatus.Passed,
                    $"{endpoint} 已连通，Google 翻译端点返回 HTTP 200。", null),
                429 => new DiagnosticResult(
                    "proxy", "代理连通性", DiagnosticStatus.Warning,
                    $"{endpoint} 已连通，但 Google 返回限流 HTTP 429。",
                    "稍后重试，或在代理策略中降低当前出口节点的请求频率。"),
                403 => new DiagnosticResult(
                    "proxy", "代理连通性", DiagnosticStatus.Warning,
                    $"{endpoint} 已连通，但 Google 拒绝请求（HTTP 403）。",
                    "更换代理节点或代理协议后重新测试。"),
                407 => new DiagnosticResult(
                    "proxy", "代理连通性", DiagnosticStatus.Failed,
                    $"{endpoint} 返回代理认证失败（HTTP 407）。",
                    "核对代理用户名与密码。"),
                _ => new DiagnosticResult(
                    "proxy", "代理连通性", DiagnosticStatus.Failed,
                    $"{endpoint} 连通，但探测端点返回 HTTP {statusCode}。",
                    "检查代理规则是否放行 translate.googleapis.com。"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticResult(
                "proxy",
                "代理连通性",
                DiagnosticStatus.Failed,
                $"{endpoint} 探测超过 12 秒。",
                "检查代理进程、网络路由与节点状态。");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                "proxy",
                "代理连通性",
                DiagnosticStatus.Failed,
                $"{endpoint} 连接失败：{Safe(ex.Message)}",
                "检查代理是否运行、端口是否正确，以及是否要求认证。");
        }
    }

    private async Task<DiagnosticResult> CheckUpdateChannelAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl))
        {
            return new DiagnosticResult(
                "update-channel",
                "更新通道与清单验签",
                DiagnosticStatus.Warning,
                "尚未配置更新清单地址，自动检查更新当前不可用。",
                "正式发布后填写 HTTPS 清单地址；开发阶段可保持未配置。");
        }

        var version = CurrentVersion();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var result = await _updates.CheckAsync(_settings.UpdateManifestUrl, version, timeout.Token);
            return ClassifyUpdateResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DiagnosticResult(
                "update-channel",
                "更新通道与清单验签",
                DiagnosticStatus.Failed,
                "更新通道探测超过 20 秒。",
                "检查网络、代理与清单服务器响应时间。");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                "update-channel",
                "更新通道与清单验签",
                DiagnosticStatus.Failed,
                $"更新通道异常：{Safe(ex.Message)}",
                "检查 HTTPS、代理与清单服务器配置。");
        }
    }

    private static DiagnosticResult ClassifyUpdateResult(UpdateCheckResult result) => result.Status switch
    {
        UpdateCheckStatus.UpToDate => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Passed,
            $"{Safe(result.Message)}；HTTPS 清单已通过 RSA-PSS 验签。", null),
        UpdateCheckStatus.Available => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Warning,
            $"{Safe(result.Message)}；HTTPS 清单已通过 RSA-PSS 验签。",
            "可到“更新与数据”页下载并安装。"),
        UpdateCheckStatus.MinimumVersionRequired => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Warning,
            Safe(result.Message), "按提示下载完整安装包，不要依赖旧版本自动替换。"),
        UpdateCheckStatus.InvalidManifest => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Failed,
            Safe(result.Message), "核对清单签名、HTTPS 地址、版本号、哈希与文件名。"),
        UpdateCheckStatus.NetworkError => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Failed,
            Safe(result.Message), "检查网络、代理与清单服务器状态。"),
        _ => new DiagnosticResult(
            "update-channel", "更新通道与清单验签", DiagnosticStatus.Warning,
            Safe(result.Message), "配置有效的 HTTPS 更新清单地址。"),
    };

    private static DiagnosticResult[] CheckUpdateInstallability()
    {
        var cacheWritable = TryWriteProbe(
            Path.Combine(AppPaths.UpdatesDirectory, $".doctor-{Guid.NewGuid():N}.tmp"),
            out var cacheError);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new DiagnosticResult(
                    "update-cache",
                    "更新缓存目录",
                    cacheWritable ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                    cacheWritable ? "更新缓存目录可写。" : $"更新缓存目录不可写：{Safe(cacheError)}",
                    cacheWritable ? null : "检查数据目录权限与磁盘空间。"),
                new DiagnosticResult(
                    "update-replace",
                    "自动替换权限",
                    DiagnosticStatus.Warning,
                    "当前由 dotnet 或未知宿主运行，不支持正式版 EXE 自动替换。",
                    "使用发布版 EXE 验证自动更新。"),
            ];
        }

        var targetDirectory = Path.GetDirectoryName(processPath);
        string? targetError = null;
        var targetWritable = targetDirectory is not null
            && TryWriteProbe(
                Path.Combine(targetDirectory, $".doctor-{Guid.NewGuid():N}.tmp"),
                out targetError);

        return
        [
            new DiagnosticResult(
                "update-cache",
                "更新缓存目录",
                cacheWritable ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                cacheWritable ? "更新缓存目录可写。" : $"更新缓存目录不可写：{Safe(cacheError)}",
                cacheWritable ? null : "检查数据目录权限与磁盘空间。"),
            new DiagnosticResult(
                "update-replace",
                "自动替换权限",
                targetWritable ? DiagnosticStatus.Passed : DiagnosticStatus.Warning,
                targetWritable
                    ? "EXE 同目录可写，安装器具备同卷替换所需权限。"
                    : $"EXE 同目录不可写：{Safe(targetError)}",
                targetWritable
                    ? null
                    : "以具备安装目录权限的方式运行，或改用可写目录中的便携版本。"),
        ];
    }

    private static bool TryWriteProbe(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "ok");
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 清理失败不影响写入权限结论。
            }
        }
    }

    private static string DisplayHotkey(string? value, HotkeyDefinition fallback) =>
        HotkeyDefinition.TryParse(value, out var definition) ? definition.ToString() : fallback.ToString();

    private static HotkeyDefinition DefaultFor(string name) => name switch
    {
        "input" => HotkeyDefinition.DefaultInput,
        "select" => HotkeyDefinition.DefaultSelect,
        "capture" => HotkeyDefinition.DefaultCapture,
        "profile" => HotkeyDefinition.DefaultProfile,
        _ => HotkeyDefinition.DefaultReplace,
    };

    private static ReleaseVersion CurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0, 0, 0);
        return new ReleaseVersion(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    private static string Safe(string? value) => DiagnosticReportFormatter.Sanitize(value);

    private sealed record HotkeySlot(
        string Id,
        string Label,
        string Name,
        string Value,
        bool Enabled);
}
