using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using RapidOcrNet;
using Serilog;
using SkiaSharp;
using TranslationApp.Core.Capture;

namespace TranslationApp.Services;

/// <summary>
/// paddle 本地高精度 OCR 引擎（FR-030 / 14.9，RapidOcrNet + PP-OCRv5 mobile 适配器）：
/// 模型三件套 + 字典作为**嵌入资源**打进 EXE（单文件、零外置、无下载），首次使用时懒加载会话：
/// 资源 byte[] → %TEMP% 落盘（用后保留复用，不每次清理重建）→ InitModels
/// （C0 实测全流程 174 ms）；空闲 5 分钟自动释放（可经 OcrPaddleResident 常驻），下次识别重建。
///
/// 【硬性配置】ONNX 会话必须 <c>EnableCpuMemArena = false</c>（见 <see cref="EnableCpuMemArena"/>）。
///
/// 【口径】识别语言由所选模型（中英日混训 rec）决定：languageTag 被忽略、**不参与 windows 引擎的
/// auto 双跑**（14.9.2 决策：paddle 是显式选择的独立引擎）；EngineTag 恒为 null（交回翻译引擎
/// 自动检测，<see cref="OcrLanguages.ReconcileWithScript"/> 脚本对账照常兜底）。
/// 初始化/推理异常（含超时：会话 ≤10s、单次识别 ≤5s）向上抛出，由 <see cref="OcrEngineRouter"/>
/// 统一降级回 windows 引擎并一次性提示（14.9.2 降级策略）。
/// </summary>
public sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    // ==================== C0 实测定档的硬性配置（改动前先读类头注释与 D:\ocrprobe\report.md §5） ====================

    /// <summary>
    /// ONNX CPU 内存 Arena 开关——**硬性 false**。C0 探针实测：默认 Arena 开启时 4MP 输入的提交内存
    /// 峰值 +624MB，超出 FR-030 的 500MB 硬顶；关闭后提交增量 +31.8MB 达标，延迟代价仅 ~5%
    /// （14.9.2 列为 C2 硬性要求，「超限即属实现缺陷」）。
    /// </summary>
    private const bool EnableCpuMemArena = false;

    /// <summary>推理线程数：C0 实测默认 2 即达标（det 136 + cls 18 + rec 326 ms，10 行样本中位 0.49s）。</summary>
    private const int IntraOpNumThreads = 2;

    /// <summary>会话初始化超时（14.9.2：会话创建 ≤10s，超时按失败计 → 路由降级）。</summary>
    private const int InitTimeoutSeconds = 10;

    /// <summary>单次识别超时（14.9.2：单次识别 ≤5s，超时按失败计 → 路由降级，不挂起 UI）。</summary>
    private const int RecognizeTimeoutSeconds = 5;

    /// <summary>嵌入资源 LogicalName 前缀（与 TranslationApp.App.csproj 的 EmbeddedResource 定义对应）。</summary>
    private const string ResourcePrefix = "TranslationApp.App.OcrModels.";

    /// <summary>PP-OCRv5 mobile 三件套 + 字典（22.5MB，C0 实测逐文件；来源 ModelScope RapidAI/RapidOCR）。</summary>
    private static readonly (string Resource, string File)[] Models =
    [
        (ResourcePrefix + "ch_PP-OCRv5_det_mobile.onnx", "ch_PP-OCRv5_det_mobile.onnx"),
        (ResourcePrefix + "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
        (ResourcePrefix + "ch_PP-OCRv5_rec_mobile.onnx", "ch_PP-OCRv5_rec_mobile.onnx"),
        (ResourcePrefix + "ppocrv5_dict.txt", "ppocrv5_dict.txt"),
    ];

    /// <summary>模型落盘目录（%TEMP% 下）。用后保留：跨次识别与跨进程重启直接复用，勿清理重建。</summary>
    private static readonly string ModelDirectory = Path.Combine(
        Path.GetTempPath(), "TranslationApp", "paddle-models");

    private static readonly SemaphoreSlim ModelExtractGate = new(1, 1);
    private static bool _modelsExtracted;

    // ==================== 懒加载会话与空闲释放 ====================

    private readonly Func<bool> _resident;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private RapidOcr? _session;
    private DateTimeOffset? _lastUsedUtc;
    private Timer? _idleTimer;
    private bool _disposed;

    /// <summary><paramref name="resident"/> 读取器绑定 <c>AppSettings.OcrPaddleResident</c>（每次判定时读取，设置即时生效）。</summary>
    public PaddleOcrEngine(Func<bool> resident)
    {
        _resident = resident;
    }

    public async Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
    {
        if (bgra.Length < width * height * BgraImage.BytesPerPixel || width <= 0 || height <= 0)
        {
            Log.Warning("PaddleOCR 输入缓冲尺寸不匹配：{Length} 字节，{Width}x{Height}", bgra.Length, width, height);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            // paddle 不参与 auto 双跑、不受识别语言设置影响（14.9.2）；日志只记设置值，无识别内容
            Log.Debug("PaddleOCR 忽略识别语言设置（{Language}）：rec 为中英日混训单模型", languageTag);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        // 全程持锁：既串行化对 ORT 会话的并发推理，也保证空闲释放不会与推理并发（ReleaseSessionCore 同锁）
        await _sessionGate.WaitAsync();
        try
        {
            var session = await GetOrCreateSessionCoreAsync();
            _lastUsedUtc = DateTimeOffset.UtcNow; // 识别开始即续期：空闲计时覆盖整个识别过程

            var stopwatch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RecognizeTimeoutSeconds));
            var result = await Task.Run(
                    () => Detect(session, bgra, width, height, timeout.Token), timeout.Token)
                .WaitAsync(timeout.Token); // 5s 硬顶：协作取消 + 等待超时双保险，超时异常 → 路由降级
            stopwatch.Stop();
            _lastUsedUtc = DateTimeOffset.UtcNow;
            EnsureIdleTimer();

            var recognition = PaddleLayoutMapper.Map(
                result.TextBlocks.Select(block => block.Text ?? string.Empty).ToArray(),
                result.TextBlocks.Select(block => block.BoxPoints
                        .Select(point => ((double)point.X, (double)point.Y))
                        .ToArray())
                    .ToArray(),
                width,
                height);
            LogRecognition(result, recognition, stopwatch.ElapsedMilliseconds);
            return recognition;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>同步检测（调用方已包 Task.Run + 超时）。BGRA → Skia 位图零拷贝包装（14.9.2）。</summary>
    private static OcrResult Detect(RapidOcr session, byte[] bgra, int width, int height, CancellationToken token)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            // 零拷贝：Skia 直接引用调用方的 BGRA 缓冲（省去 4MP 级 ~16MB 位图复制），识别期间由 GCHandle 固定
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
            {
                throw new InvalidOperationException($"Skia InstallPixels 失败（{width}x{height}）");
            }

            // RapidOcrOptions.Default：LimitSideLen=736 + DoAngle=True + TextScore=0.5（C0 实测口径，
            // 同时是推理峰值内存硬顶 500MB 的约束项，见 14.9.2）
            return session.Detect(bitmap, RapidOcrOptions.Default, token);
        }
        finally
        {
            handle.Free();
        }
    }

    // ==================== 懒加载 / 释放（时序判定纯逻辑在 PaddleSessionPolicy） ====================

    /// <summary>取当前会话；无则建（懒加载）。调用方必须已持有 <see cref="_sessionGate"/>。</summary>
    private async Task<RapidOcr> GetOrCreateSessionCoreAsync()
    {
        if (_session is not null)
        {
            return _session;
        }

        Log.Information("PaddleOCR 首次使用：建立模型会话（懒加载，常驻={Resident}）", _resident());
        var creation = Task.Run(CreateSession);
        RapidOcr session;
        try
        {
            session = await creation.WaitAsync(TimeSpan.FromSeconds(InitTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            // 超时判负后后台会话仍可能建成：挂释放回调回收孤儿会话（~30MB），失败按降级策略处理
            _ = creation.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    try { t.Result.Dispose(); } catch { /* 孤儿会话回收失败不影响主链路 */ }
                }
            }, TaskScheduler.Default);
            throw;
        }

        _session = session;
        _lastUsedUtc = DateTimeOffset.UtcNow;
        Log.Information("PaddleOCR 模型会话已建立（常驻={Resident}，空闲 {Minutes:0} 分钟后自动释放）",
            _resident(), PaddleSessionPolicy.IdleReleaseDelay.TotalMinutes);
        return session;
    }

    /// <summary>建会话：模型落盘 → InitModels（硬性 EnableCpuMemArena=false，见 <see cref="EnableCpuMemArena"/>）。</summary>
    private static RapidOcr CreateSession()
    {
        ExtractModelsToTemp();

        var session = new RapidOcr();
        var options = RapidOcr.GetDefaultSessionOptions(IntraOpNumThreads);
        options.EnableCpuMemArena = EnableCpuMemArena;
        session.InitModels(
            Path.Combine(ModelDirectory, Models[0].File),
            Path.Combine(ModelDirectory, Models[1].File),
            Path.Combine(ModelDirectory, Models[2].File),
            Path.Combine(ModelDirectory, Models[3].File),
            options);
        return session;
    }

    /// <summary>
    /// 嵌入资源 → %TEMP% 落盘（进程内一次）。**保留复用**：同名同长的落盘文件直接跳过
    /// （14.9.1/14.9.2：勿每次清理重建）；长度不符（截断/杀软破坏）时重写。
    /// </summary>
    private static void ExtractModelsToTemp()
    {
        if (_modelsExtracted)
        {
            return;
        }

        ModelExtractGate.Wait();
        try
        {
            if (_modelsExtracted)
            {
                return;
            }

            Directory.CreateDirectory(ModelDirectory);
            var assembly = typeof(PaddleOcrEngine).Assembly;
            foreach (var (resource, file) in Models)
            {
                using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException(
                        $"PaddleOCR 模型嵌入资源缺失：{resource}（EXE 被裁剪或安全软件误删）");
                var target = Path.Combine(ModelDirectory, file);
                if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
                {
                    continue;
                }

                using var output = File.Create(target);
                stream.CopyTo(output);
            }

            _modelsExtracted = true;
        }
        finally
        {
            ModelExtractGate.Release();
        }
    }

    /// <summary>确保空闲释放定时器存在（仅非常驻；常驻时永不建，已存在也不变）。</summary>
    private void EnsureIdleTimer()
    {
        if (!_resident())
        {
            _idleTimer ??= new Timer(OnIdleTimer, null, PaddleSessionPolicy.IdleReleaseDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnIdleTimer(object? state)
    {
        // 时序判定纯逻辑在 Core（PaddleSessionPolicy）；识别进行中（TryWait 失败）会续期计时，直接跳过
        if (!PaddleSessionPolicy.ShouldRelease(_lastUsedUtc, DateTimeOffset.UtcNow, _resident())
            || !_sessionGate.Wait(0))
        {
            return;
        }

        try
        {
            if (PaddleSessionPolicy.ShouldRelease(_lastUsedUtc, DateTimeOffset.UtcNow, _resident())
                && _session is not null)
            {
                _session.Dispose();
                _session = null;
                Log.Information("PaddleOCR 模型会话已释放（空闲 {Minutes:0} 分钟，下次识别将自动重建）",
                    PaddleSessionPolicy.IdleReleaseDelay.TotalMinutes);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PaddleOCR 空闲会话释放失败（不影响后续识别）");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ==================== 日志（14.9.2：引擎名、耗时分解、行数、字符数、词框面积、内存峰值；不记内容） ====================

    private static void LogRecognition(OcrResult result, OcrRecognition recognition, long wallMs)
    {
        var clsMs = result.TextBlocks.Sum(block => block.AngleTime);
        var recMs = result.TextBlocks.Sum(block => block.CrnnTime);
        var chars = recognition.Lines.Sum(line => line.Text?.Length ?? 0);
        var area = recognition.Lines.Sum(line => line.Rect.Area);
        var privateMb = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0;
        Log.Information(
            "PaddleOCR 识别完成（det {Det:0} ms + cls {Cls:0} ms + rec {Rec:0} ms，总耗时 {Total:0} ms，"
            + "{Lines} 行，{Chars} 字，词框 {Area:0} px²，TextAngle={Angle}，私有内存 {MemoryMb:0.#} MB）",
            result.DbNetTime, clsMs, recMs, wallMs,
            recognition.Lines.Count, chars, area,
            recognition.TextAngle is { } angle ? angle.ToString("0.###") : "null",
            privateMb);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();
        _idleTimer = null;
        _sessionGate.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
        }
    }
}
