using System.IO;
using System.Text;
using TranslationApp.Core.Settings;
using TranslationApp.Setup.Interop;

namespace TranslationApp.Setup;

/// <summary>可选的翻译引擎（与主程序 TranslatorCatalog 里的 Id 一一对应）。</summary>
internal sealed record EngineOption(string Id, string Name, bool NeedsKey, string Note)
{
    /// <summary>设计稿里「接引擎」一页的候选与顺序。</summary>
    public static readonly EngineOption Bing =
        new("bing", "Bing", false, "Bing 免密钥，开箱可用，不需要任何配置。");

    public static readonly EngineOption Tencent =
        new("tencent", "腾讯云 TMT", true, "需要 SecretId 与 SecretKey——在腾讯云控制台建一个机器翻译应用，把两串密钥粘进来。");

    public static readonly EngineOption Baidu =
        new("baidu", "百度翻译", true, "需要 APPID 与密钥——在百度翻译开放平台申请通用翻译 API 之后粘贴。");

    public static readonly EngineOption DeepL =
        new("deepl", "DeepL", true, "需要 API Key——Free 与 Pro 版都能用，粘贴即可。");

    public static readonly EngineOption Llm =
        new("llm", "AI 引擎", true, "需要 OpenAI 兼容的 Base URL 与 Key——可以接自建模型。");

    public static readonly IReadOnlyList<EngineOption> All = [Bing, Tencent, Baidu, DeepL, Llm];

    /// <summary>引擎列表里的角标（免密钥 / 需密钥 / 兼容）。</summary>
    public string Badge => Id switch
    {
        "bing" => "免密钥",
        "llm" => "兼容",
        _ => "需密钥",
    };

    /// <summary>核对格与契书上的短名。</summary>
    public string ShortName => NeedsKey ? $"{Name} · 需密钥" : $"{Name} · 免密钥";
}

/// <summary>立契时定下的全部决定。</summary>
internal sealed record InstallOptions(
    string InstallDirectory,
    bool AutoStart,
    bool DesktopShortcut,
    bool StartMenuShortcut,
    EngineOption Engine);

/// <summary>一件真实工作项：标题 + 右侧注脚 + 真正要做的事。</summary>
internal sealed record InstallStep(string Title, string Note, Func<CancellationToken, Task> Execute);

/// <summary>安装报告：契书上写什么，全部来自这里，没有一个是手写的。</summary>
internal sealed record InstallReport(
    string InstallDirectory,
    string DataDirectory,
    EngineOption Engine,
    bool AutoStart,
    bool DesktopShortcut,
    bool StartMenuShortcut,
    long PayloadBytes,
    string Version,
    TimeSpan Elapsed);

/// <summary>
/// 立契的执行者：把「盖印」一页列出的每一件工作真正做掉。
/// 全程只动当前用户范围内的东西——用户目录、HKCU、快捷方式，
/// 所以不需要管理员，也不会给系统留一处说不清来历的改动。
/// </summary>
internal sealed class InstallService
{
    private readonly PayloadStore _payload;

    public InstallService(PayloadStore payload) => _payload = payload;

    /// <summary>预计占用（字节）。载荷是安装目录里唯一的实质文件。</summary>
    public long PayloadBytes => _payload.SizeBytes;

    public string Version => _payload.Version;

    /// <summary>按当前决定列出这一遍要做的全部工作项（关掉的选项不会出现在清单里）。</summary>
    public IReadOnlyList<InstallStep> BuildPlan(InstallOptions options)
    {
        var steps = new List<InstallStep>
        {
            new(
                "校验载荷完整性",
                _payload.ExpectedSha256.Length > 0 ? "单文件 · SHA-256" : "单文件 · 未签名构建",
                VerifyPayloadAsync),
            new(
                "写入安装目录",
                $"{FormatSize(_payload.SizeBytes)}",
                ct => Task.Run(() => WritePayload(options.InstallDirectory), ct)),
        };

        if (options.StartMenuShortcut || options.DesktopShortcut)
        {
            var where = (options.StartMenuShortcut, options.DesktopShortcut) switch
            {
                (true, true) => "开始菜单 · 桌面",
                (true, false) => "开始菜单",
                _ => "桌面",
            };
            steps.Add(new InstallStep("建立快捷方式", where,
                ct => Task.Run(() => WriteShortcuts(options), ct)));
        }

        steps.Add(new InstallStep("写入落址与引擎配置", "settings.json",
            ct => Task.Run(() => WriteSettings(options), ct)));

        if (options.AutoStart)
        {
            steps.Add(new InstallStep("登记开机自启", "仅托盘 · Alt+D",
                ct => Task.Run(() => InstallLayout.SetAutoStart(options.InstallDirectory, true), ct)));
        }

        steps.Add(new InstallStep("登记收印入口", "应用和功能",
            ct => Task.Run(() => WriteUnsealEntry(options), ct)));

        return steps;
    }

    // ------------------------------------------------------------
    // 逐项实现
    // ------------------------------------------------------------
    private Task VerifyPayloadAsync(CancellationToken ct)
    {
        if (!_payload.Present)
        {
            throw new InvalidOperationException(
                "这个安装包里没有主程序（开发构建未跑发布）。请用 build\\make-setup.ps1 重新打包。");
        }

        if (_payload.ExpectedBytes > 0 && _payload.SizeBytes != _payload.ExpectedBytes)
        {
            throw new InvalidOperationException(
                $"载荷大小与打包登记不一致（{_payload.SizeBytes} ≠ {_payload.ExpectedBytes} 字节）。");
        }

        var expected = _payload.ExpectedSha256;
        if (expected.Length == 0)
        {
            // 开发构建没有登记指纹：退一步做最小可信检查——载荷得是一个 PE 文件
            using var stream = _payload.Open();
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidOperationException("载荷不是可执行文件（缺少 MZ 头）。");
            }

            return Task.CompletedTask;
        }

        var actual = _payload.ComputeSha256();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("载荷指纹与打包登记不一致，安装包可能在传输中损坏或被调包。");
        }

        return Task.CompletedTask;
    }

    private void WritePayload(string installDirectory)
    {
        var target = InstallLayout.AppPath(installDirectory);
        Directory.CreateDirectory(installDirectory);

        // 先落 .tmp 再改名：中途失败不会在安装目录里留下一个半截的 EXE，
        // 用户看到的是一个「要么没有、要么能用」的落址。
        var staging = target + ".tmp";
        try
        {
            _payload.ExtractTo(staging);
            File.Move(staging, target, overwrite: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"落址不可写：{installDirectory}。换一个当前用户可写的目录（默认在 %LocalAppData%\\Programs 下）。", ex);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private static void WriteShortcuts(InstallOptions options)
    {
        var app = InstallLayout.AppPath(options.InstallDirectory);
        if (options.StartMenuShortcut)
        {
            ShellLink.Create(InstallLayout.StartMenuLink, app, InstallLayout.ProductName);
        }

        if (options.DesktopShortcut)
        {
            ShellLink.Create(InstallLayout.DesktopLink, app, InstallLayout.ProductName);
        }
    }

    private static void WriteSettings(InstallOptions options)
    {
        var settingsPath = Path.Combine(InstallLayout.DataDirectory(options.InstallDirectory), "settings.json");
        var store = new JsonSettingsStore(settingsPath);
        // 覆盖安装时保留用户已有的全部设置，只把这次明确选定的引擎写进去
        var settings = File.Exists(settingsPath) ? store.Load() : new AppSettings();
        settings.Engine = options.Engine.Id;
        store.Save(settings);
    }

    private static void WriteUnsealEntry(InstallOptions options)
    {
        var dataDirectory = InstallLayout.DataDirectory(options.InstallDirectory);
        UnsealScript.Write(options.InstallDirectory, dataDirectory);
        InstallLayout.WriteUninstallEntry(options.InstallDirectory, PayloadStore.Current.Version, PayloadStore.Current.SizeBytes);
    }

    // ------------------------------------------------------------
    // 收印（卸载）
    // ------------------------------------------------------------
    /// <summary>
    /// 抹掉这次安装写下的全部痕迹。userData 为 true 时连印谱与藏印一并抹去。
    /// 只删「登记过的落址」，不碰别处——用户把程序挪走了，我们不会顺手删掉别的目录。
    /// </summary>
    public static void Unseal(string installDirectory, bool userData)
    {
        var dataDirectory = InstallLayout.DataDirectory(installDirectory);

        StopRunningApp(installDirectory);
        InstallLayout.SetAutoStart(installDirectory, false);
        InstallLayout.RemoveUninstallEntry();
        DeleteFile(InstallLayout.StartMenuLink);
        DeleteFile(InstallLayout.DesktopLink);
        DeleteDirectory(installDirectory);

        if (userData)
        {
            DeleteDirectory(dataDirectory);
        }
    }

    /// <summary>停掉正在跑的译印实例，否则安装目录里的 EXE 被文件锁占着，删不干净。</summary>
    public static void StopRunningApp(string installDirectory)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("TranslationApp"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null || !path.StartsWith(installDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // 权限不足或进程已退出：删文件那一步会给出最终结论，这里不喧宾夺主
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 快捷方式被占用时留着不影响功能
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 收印时目录被占用（例如用户正开着资源管理器）不阻断其余清理
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
            : $"{bytes / 1024.0 / 1024:0.#} MB";
}

/// <summary>
/// 生成安装目录里的收印脚本。用脚本而不是把安装器本体复制进去：
/// 安装器带着 100MB 载荷，为了一次卸载在用户机器上再留一份是浪费。
/// 脚本以 UTF-8 BOM 落盘——PowerShell 5.1 读无 BOM 的 UTF-8 会把中文读成乱码。
/// </summary>
internal static class UnsealScript
{
    public static void Write(string installDirectory, string dataDirectory)
    {
        var path = InstallLayout.UnsealScriptPath(installDirectory);
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(path, Build(installDirectory, dataDirectory), new UTF8Encoding(true));
    }

    private static string Build(string installDirectory, string dataDirectory) =>
        """
        #Requires -Version 5.1
        <#
          译印 INKSEAL · 收印
          由「应用和功能」调用。把装进来的东西原样抹掉，不留残。
        #>
        $ErrorActionPreference = 'SilentlyContinue'

        $InstallDir = '__INSTALL_DIR__'
        $DataDir    = '__DATA_DIR__'

        # 脚本自己就住在安装目录里，直接删会被自己的文件句柄挡住：
        # 先复制到临时目录，再从那里执行真正的清理。
        if (-not $env:INKSEAL_UNSEAL_TEMP) {
            $tmp = Join-Path $env:TEMP ('inkseal-unseal-' + [guid]::NewGuid().ToString('N') + '.ps1')
            Copy-Item -LiteralPath $PSCommandPath -Destination $tmp -Force
            $env:INKSEAL_UNSEAL_TEMP = '1'
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $tmp
            exit $LASTEXITCODE
        }

        Add-Type -AssemblyName PresentationFramework
        $answer = [System.Windows.MessageBox]::Show(
            "将收回这方印：`n`n  落址  $InstallDir`n  印谱  $DataDir`n`n点「是」连印谱与藏印一并抹去；点「否」只收程序，保留你的翻译记录。",
            '译印 INKSEAL · 收印',
            'YesNoCancel',
            'Warning')

        if ($answer -eq 'Cancel') { exit 0 }

        # 正在跑的实例会锁住安装目录
        Get-Process -Name 'TranslationApp' -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                if ($_.MainModule.FileName -like "$InstallDir*") { $_.Kill() }
            } catch { }
        }
        Start-Sleep -Milliseconds 400

        # 快捷方式
        $links = @(
            (Join-Path ([Environment]::GetFolderPath('Programs')) '译印 INKSEAL.lnk'),
            (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) '译印 INKSEAL.lnk')
        )
        foreach ($l in $links) { Remove-Item -LiteralPath $l -Force }

        # 开机自启
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TranslationApp' -Force

        # 「应用和功能」里的登记
        Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\译印 INKSEAL' -Recurse -Force

        # 程序本体
        Remove-Item -LiteralPath $InstallDir -Recurse -Force

        # 印谱与藏印（用户选了「是」才动）
        if ($answer -eq 'Yes') {
            Remove-Item -LiteralPath $DataDir -Recurse -Force
            [System.Windows.MessageBox]::Show('印已收回，本机不再留痕。', '译印 INKSEAL · 收印', 'OK', 'Information') | Out-Null
        } else {
            [System.Windows.MessageBox]::Show("印已收回，程序已移除。`n你的印谱与藏印保留在：`n$DataDir", '译印 INKSEAL · 收印', 'OK', 'Information') | Out-Null
        }
        """
        .Replace("__INSTALL_DIR__", installDirectory.Replace("'", "''"))
        .Replace("__DATA_DIR__", dataDirectory.Replace("'", "''"));
}

/// <summary>无人值守收印（安装器自身的 --unseal 分支）。</summary>
internal static class Uninstaller
{
    public static bool RunSilently()
    {
        if (!InstallLayout.TryGetInstalledDirectory(out var installDirectory))
        {
            return false;
        }

        InstallService.Unseal(installDirectory, userData: false);
        return true;
    }
}
