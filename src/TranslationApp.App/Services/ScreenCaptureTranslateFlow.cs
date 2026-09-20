using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Serilog;
using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using TranslationApp.Core.Settings;
using TranslationApp.Core.Translation;
using TranslationApp.Interop;
using TranslationApp.Windows;

namespace TranslationApp.Services;

/// <summary>
/// 截图翻译流程编排（FR-021 / 13.2.3 八步状态机 + FR-027 / 14.3.8 分流）：
/// 记录前台窗口 → 取鼠标所在显示器 → 截屏 → 显示遮罩框选 → 裁剪（必要时等比缩小）→ OCR（**带布局**）→
/// 按 <c>OcrOutputMode</c> 分流：默认 <c>pin</c> 把选区**钉在屏幕上原位置并原位显示译文**，
/// <c>text</c> 保持 FR-021 旧链路（文本进小窗），<c>both</c> 先钉图再开小窗。
///
/// 本批（FR-027c）新增的 pin 链路（14.3.2 / 14.3.5）：
/// 行框并集 → <see cref="OcrBlockGrouping"/> 聚块（**按块翻译**，逐行翻译会切断句子且行数必然对不上）→
/// 底色取样 <see cref="BackgroundSampler"/> → 排版与降级决策 <see cref="OverlayLayout"/> → 钉图。
/// 任何一步失败都只提示并退出流程，绝不冒泡（需求 6 可靠性）；取消时不留任何残留窗口。
/// </summary>
public sealed class ScreenCaptureTranslateFlow
{
    private readonly AppSettings _settings;
    private readonly OcrService _ocr;
    private readonly QuickWindow _quickWindow;
    private readonly PinWindowManager _pins;
    private readonly TranslatorCatalog _catalog;
    private readonly Action<string, string> _balloon;

    private bool _running;

    public ScreenCaptureTranslateFlow(
        AppSettings settings,
        OcrService ocr,
        QuickWindow quickWindow,
        PinWindowManager pins,
        TranslatorCatalog catalog,
        Action<string, string> balloon)
    {
        _settings = settings;
        _ocr = ocr;
        _quickWindow = quickWindow;
        _pins = pins;
        _catalog = catalog;
        _balloon = balloon;
    }

    /// <summary>热键/托盘/小窗按钮统一入口；重复触发时忽略（正在框选或识别中）。</summary>
    public async Task StartAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图翻译流程异常");
            _balloon("速译 - 截图翻译", "截图翻译失败，请重试（详见日志）");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task RunAsync()
    {
        // 13.2.5：语言包缺失不得让热键静默失效，明确提示安装指引后退出流程
        if (!_ocr.IsAvailable)
        {
            Log.Warning("按下截图热键，但系统未安装 OCR 语言包");
            _balloon("速译 - 截图翻译不可用", OcrLanguages.MissingPackMessage);
            return;
        }

        if (!TryResolveMonitor(out var monitor, out var monitorInfo))
        {
            _balloon("速译 - 截图翻译", "无法确定鼠标所在的显示器，截图已取消");
            return;
        }

        // 先收起小窗：避免它被截进画面，同时保留已输入的文本以便识别后追加（FR-021）
        var previousInput = _quickWindow.PrepareForCapture();
        // 收起小窗之后再记录前台窗口：此时前台已是被小窗遮挡前的那个应用（AC 3 焦点还原）
        var previousForeground = NativeMethods.GetForegroundWindow();

        // 步骤 3：必须在遮罩显示之前截屏，否则会把遮罩自身截进去（AC 8）
        var width = monitorInfo.RcMonitor.Right - monitorInfo.RcMonitor.Left;
        var height = monitorInfo.RcMonitor.Bottom - monitorInfo.RcMonitor.Top;
        var monitorRect = new PhysicalRect(
            monitorInfo.RcMonitor.Left, monitorInfo.RcMonitor.Top, width, height);
        var frame = ScreenCapturer.Capture(monitorInfo.RcMonitor.Left, monitorInfo.RcMonitor.Top, width, height);
        if (frame is null)
        {
            _balloon("速译 - 截图翻译", "截屏失败，请重试");
            return;
        }

        // 步骤 4~6：遮罩框选（松开即确认；Esc / 右键 / 过小选区 = 取消）
        var overlay = new CaptureOverlayWindow(
            frame.Display, monitorInfo.RcMonitor, _settings.OcrScrimOpacity, _ocr.MaxImageDimension);
        var confirmed = overlay.ShowDialog() == true;

        if (!confirmed || overlay.ConfirmedSelection is not { } selection)
        {
            Log.Debug("截图翻译已取消");
            RestoreForeground(previousForeground);
            return;
        }

        // 步骤 7：DIP 选区 → 图像像素矩形（实测比值换算）→ 裁剪 → 超限等比缩小 → 识别
        var imageRect = CaptureGeometry.DipToImageRect(
            selection, overlay.ScaleX, overlay.ScaleY, frame.Width, frame.Height);
        var cropped = BgraImage.Crop(frame.Bgra, frame.Width, frame.Height, imageRect);
        if (cropped.Length == 0)
        {
            _balloon("速译 - 截图翻译", "选区无效，截图已取消");
            RestoreForeground(previousForeground);
            return;
        }

        // 钉图用的是**识别前的原裁剪图**：MaxImageDimension 的缩小只为满足引擎输入限制，不应影响钉图观感
        var pinBuffer = cropped;
        var fit = CaptureGeometry.FitToMaxDimension(imageRect.Width, imageRect.Height, _ocr.MaxImageDimension);
        if (fit.Downscaled)
        {
            Log.Information("选区超过 MaxImageDimension={Max}，按 {Ratio:0.###} 倍缩小后识别（{From} → {To}）",
                _ocr.MaxImageDimension, fit.Ratio, $"{imageRect.Width}x{imageRect.Height}", $"{fit.Width}x{fit.Height}");
            cropped = BgraImage.Resize(cropped, imageRect.Width, imageRect.Height, fit.Width, fit.Height);
        }

        var status = _ocr.ResolveStatus(_settings.OcrLanguage);
        var recognition = await _ocr.RecognizeWithLayoutAsync(cropped, fit.Width, fit.Height, status.SelectedTag);

        // FR-029-1（14.3.12.4）预处理增强：上一行原图识别与旧版本逐字节一致（零回归）。
        // 仅当命中触发条件（auto）或设置 on 时才对增强图重跑一次，按 OcrScriptScoring 择优；
        // 增强路径整体故障隔离（异常/无结果一律回原候选），增强候选的还原比例 = fit.Ratio / scale。
        var preprocess = await OcrPreprocess.RunAsync(
            ToPreprocessCandidate(recognition), cropped, fit.Width, fit.Height, fit.Ratio,
            _settings.OcrPreprocess,
            async (bgra, width, height) =>
            {
                var rerun = await _ocr.RecognizeWithLayoutAsync(bgra, width, height, status.SelectedTag);
                return ToPreprocessCandidate(rerun);
            });
        LogPreprocess(preprocess);
        if (preprocess.EnhancedUsed)
        {
            recognition = new OcrRecognition(
                preprocess.Candidate.Text ?? string.Empty,
                preprocess.Candidate.Lines,
                preprocess.Candidate.TextAngle,
                preprocess.Candidate.EngineTag);
        }

        var text = recognition?.Text;

        if (text is null || recognition is null)
        {
            _balloon("速译 - 截图翻译", "OCR 识别失败，请重试或检查语言包");
            return;
        }

        // C-⑥：以**实际使用的识别器语言**回传翻译源语言——auto 双跑选中 en-US 时 source="en"，
        // 英文文本正确送英→中翻译，而不是沿用 auto 让引擎检测成 zh 后近直通；
        // 「识别语言 == 目标语言则跳过翻译」的判定（ShouldSkipTranslation）同样用它。
        // 映射不出来（如引擎回退到未收录语言）时保持原 status 的语义（auto = 引擎自动检测）。
        // ③-B 对账：映射结果与文本脚本方向性冲突（如 zh 引擎把英文识别成 CJK 乱码）时交回自动检测，
        // 避免「识别语言=目标语言」的误跳过与反向的强制错源。
        if (!string.IsNullOrEmpty(recognition.EngineTag))
        {
            var mapped = OcrLanguages.TryMapToTranslationCode(recognition.EngineTag);
            if (mapped is not null)
            {
                var reconciled = OcrLanguages.ReconcileWithScript(mapped, text);
                if (reconciled is null)
                {
                    Log.Information(
                        "OCR 语言映射与文本脚本冲突，保持自动检测（引擎={Engine}，映射={Mapped}，脚本得分 {Score:0.###}）",
                        recognition.EngineTag, mapped, OcrScriptScoring.Score(text));
                }
                else
                {
                    status = status with { TranslationCode = reconciled };
                }
            }
        }

        // 只记元数据，不记录识别出的内容（需求 6 安全：日志脱敏）
        Log.Information(
            "截图识别完成（设置={Language}，引擎={Engine}，回退={Fallback}，选区 {Width}x{Height}，{Length} 字符，{Lines} 行）",
            status.SelectedTag, recognition.EngineTag, status.IsFallback, imageRect.Width, imageRect.Height,
            text?.Length ?? 0, recognition.Lines.Count);

        var notice = status.IsFallback ? OcrLanguages.DescribeFallback(status.Available) : null;
        var mode = OcrOutputMode.Parse(_settings.OcrOutputMode);

        // 无文字：与 13.2.3 一致地提示；pin 路径没有小窗可填，故走气泡
        if (string.IsNullOrWhiteSpace(text))
        {
            const string emptyNotice = "未识别到文字，请重新截图或手动输入";
            if (OcrOutputMode.UsesQuickWindow(mode))
            {
                _quickWindow.ShowForOcrText("", null, previousInput, notice ?? emptyNotice);
            }
            else
            {
                _balloon("速译 - 截图翻译", emptyNotice);
            }

            return;
        }

        // FR-027（14.3.8 新链路，默认）：把选区图片钉在屏幕上原位置，并**原位显示译文**。
        // 钉图**不激活、不抢焦点**，故把焦点还给呼出前的应用
        if (OcrOutputMode.PinsImage(mode))
        {
            var pinned = await BuildPinAsync(
                recognition, imageRect, monitorInfo, preprocess.RestoreRatio, status, previousInput, notice, pinBuffer);
            _pins.TryPin(
                pinBuffer, imageRect, monitorRect, pinned.Content, pinned.RetryAsync,
                pinned.OpenInQuickWindow, pinned.ForceTranslateAsync);

            if (!OcrOutputMode.UsesQuickWindow(mode))
            {
                RestoreForeground(previousForeground);
            }
        }

        // 步骤 8（旧链路，text / both）：识别文本进入小窗可编辑输入框，是否自动翻译由 OcrAutoTranslate 决定
        if (OcrOutputMode.UsesQuickWindow(mode))
        {
            _quickWindow.ShowForOcrText(text, status.TranslationCode, previousInput, notice);
        }
    }

    // ==================== pin 链路：布局 → 翻译 → 排版 ====================

    /// <summary><see cref="OcrRecognition"/> → 预处理候选快照（字段一一对应，无损往返）。</summary>
    private static OcrPreprocessCandidate? ToPreprocessCandidate(OcrRecognition? recognition) =>
        recognition is null
            ? null
            : new OcrPreprocessCandidate(recognition.Text, recognition.Lines, recognition.TextAngle, recognition.EngineTag);

    /// <summary>预处理决策日志：级别与内容都由 Core 管线给出（Information 级、无识别内容，沿用 auto 双跑格式）。</summary>
    private static void LogPreprocess(OcrPreprocessOutcome outcome)
    {
        switch (outcome.Level)
        {
            case OcrPreprocessLogLevel.Warning:
                Log.Warning(outcome.Error, "{Message}", outcome.LogMessage);
                break;
            case OcrPreprocessLogLevel.Debug:
                Log.Debug("{Message}", outcome.LogMessage);
                break;
            default:
                Log.Information("{Message}", outcome.LogMessage);
                break;
        }
    }

    /// <summary>
    /// 把识别结果变成钉图内容：布局还原 → 聚块 → 按块翻译 → 底色取样 → 排版与降级。
    /// 返回的内容还带两个回调：**重试**（翻译失败后补齐译文，AC 12）与**在小窗中打开**（14.3.8）。
    /// </summary>
    private async Task<PinnedPlan> BuildPinAsync(
        OcrRecognition recognition,
        PixelRect imageRect,
        NativeMethods.MONITORINFO monitorInfo,
        double fitRatio,
        OcrLanguageStatus status,
        string? previousInput,
        string? notice,
        byte[] pinPixels)
    {
        // 坐标从「识别时的位图」还原回**原裁剪图像素**（14.3.1 第 2 条：x = rect.X / fit.Ratio）
        var layout = OcrLayoutRules.Create(
            recognition.Lines, recognition.TextAngle, imageRect.Width, imageRect.Height, fitRatio);
        var blocks = OcrBlockGrouping.Group(layout.Lines, layout.LineHeight, imageRect.Width, imageRect.Height);
        var dpiScale = ResolveDpiScale(imageRect, monitorInfo);

        // 不可能原位替换的情形（倾斜 / 竖排 / 语言相同）→ 直接交给降级判定，不再翻译
        var forced = ResolveForcedSidePanelReason(layout, blocks, status);
        // 「识别语言与目标语言相同」的跳过态仍给用户一个**强制翻译**的补救入口（v1.2 修复批 ③-C）
        var languageSkipped = string.Equals(forced, LanguageSkippedReason, StringComparison.Ordinal);

        // 只记元数据与块数/模式，不记识别与译文内容（14.3.10 日志脱敏）
        Log.Information(
            "OCR 布局：{Lines} 行 → {Blocks} 块，TextAngle={Angle}，行高中位数 {LineHeight:0.#} px，屏缩放 {Scale}，降级={Forced}",
            layout.Lines.Count, blocks.Count,
            recognition.TextAngle is { } angle ? angle.ToString("0.###", CultureInfo.InvariantCulture) : "null",
            layout.LineHeight, dpiScale, forced ?? "无");

        // 底色取样取的是**原裁剪图**（钉图用的就是它；缩小过的坐标已还原，两者同为原裁剪图像素）
        var samples = BackgroundSampler.SampleAll(pinPixels, imageRect.Width, imageRect.Height, blocks);

        // FR-048：段框内部聚类采样文字墨色（识别线程一次算完，重试/强制翻译复用同一份，不增加交互延迟）
        var inks = SampleInks(pinPixels, imageRect.Width, imageRect.Height, blocks, samples);

        // 强制翻译 = 把源语言置回**自动检测**后，对同一批块重新走「翻译 + 排版」（倾斜 / 竖排的降级不在此列）
        Func<Task<PinContent>>? forceTranslate = languageSkipped && blocks.Count > 0
            ? () => RetranslateAsync(
                blocks, samples, dpiScale, status with { TranslationCode = null },
                imageRect.Width, imageRect.Height, recognition.Text, inks)
            : null;

        var outcome = forced is null && blocks.Count > 0
            ? await TranslateBlocksAsync(blocks, status, CancellationToken.None)
            : BlockTranslation.None(blocks.Count);

        var plan = OverlayLayout.Build(
            new OverlayRequest(
                blocks,
                outcome.Texts,
                imageRect.Width,
                imageRect.Height,
                dpiScale,
                samples,
                outcome.MergedText,
                forced,
                outcome.SegmentMismatch,
                outcome.Failed,
                inks),
            MeasureText);

        var sourceText = recognition.Text;
        var imageWidth = imageRect.Width;
        var imageHeight = imageRect.Height;
        var textAngle = layout.TextAngle;

        // 翻译缺失（失败或部分失败）时才提供重试：点一次就重新翻译并刷新这张钉图（AC 12）
        var canRetry = forced is null && blocks.Count > 0 && !outcome.HasTranslation && IsTranslatable(status);
        Func<Task<PinContent>>? retry = canRetry
            ? () => RetranslateAsync(blocks, samples, dpiScale, status, imageWidth, imageHeight, sourceText, inks)
            : null;

        // 「在小窗中打开」= 把识别文本带回主流程（可编辑、可再翻译、可入库），14.3.8
        Action? openInQuick = () =>
            _quickWindow.ShowForOcrText(sourceText, status.TranslationCode, previousInput, notice);

        // 面积比与逐块几何（只在 --verbose 下输出）：**降级到底为什么发生**必须能直接从日志量化出来，
        // 否则「短选区被降级成整块排版」这类问题只能靠猜。只记几何、字号与字符数，不记原文/译文内容（14.3.10）
        Log.Debug(
            "钉图面积比：{Ratio}（阈值 {Limit}），块明细 [{Blocks}]",
            plan.AreaRatio is { } ratio
                ? ratio.ToString("0.###", CultureInfo.InvariantCulture)
                : "未计算（链路已降级或无块）",
            OverlayLayout.AreaRatioLimit,
            blocks.Count == 0
                ? "无"
                : string.Join(" | ", blocks.Select(block => string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0} ({1:0.#},{2:0.#},{3:0.#},{4:0.#})px 字号{5:0.#} 原文{6}字",
                    block.Index, block.Rect.Left, block.Rect.Top, block.Rect.Width, block.Rect.Height,
                    block.EstimatedFontPx, block.SourceText?.Length ?? 0))));

        Log.Information(
            "钉图排版：模式 {Mode}，覆盖块 {Blocks} 个，面板={HasPanel}，重试={CanRetry}，强制翻译={CanForce}",
            plan.Mode, plan.Blocks.Count, !string.IsNullOrEmpty(plan.PanelText), retry is not null,
            forceTranslate is not null);

        var content = ToContent(plan, recognition.Text, outcome);
        if (forceTranslate is not null)
        {
            content = content with { CanForceTranslate = true };
        }

        return new PinnedPlan(content, retry, openInQuick, forceTranslate);
    }

    /// <summary>重试：重新翻译同一批块并刷新钉图内容（钉图不重建，位置与缩放不变）。</summary>
    private async Task<PinContent> RetranslateAsync(
        IReadOnlyList<OcrBlock> blocks,
        IReadOnlyList<BackgroundSample> samples,
        double dpiScale,
        OcrLanguageStatus status,
        int imageWidth,
        int imageHeight,
        string? sourceText,
        IReadOnlyList<uint?>? inks = null)
    {
        var outcome = await TranslateBlocksAsync(blocks, status, CancellationToken.None);
        if (!outcome.HasTranslation)
        {
            // 仍然失败：保持原图 + 说明 + 可继续重试，绝不因为一次网络抖动丢掉用户的截图
            return new PinContent(
                [], sourceText, null, DescribeFailure(outcome) ?? "翻译失败，请稍后重试")
            {
                CanRetry = true,
            };
        }

        var plan = OverlayLayout.Build(
            new OverlayRequest(
                blocks, outcome.Texts, imageWidth, imageHeight, dpiScale, samples,
                outcome.MergedText, null, outcome.SegmentMismatch, false, inks),
            MeasureText);
        return ToContent(plan, sourceText, outcome);
    }

    /// <summary>
    /// FR-048：逐块在段框内做颜色聚类取文字墨色（只算像素统计，不碰原文/译文内容，14.3.10 口径）。
    /// 采样失败项为 null → 界面按底色深浅兜底黑/白，绝不因此改变排版。
    /// </summary>
    private static IReadOnlyList<uint?> SampleInks(
        byte[] bgra, int width, int height, IReadOnlyList<OcrBlock> blocks, IReadOnlyList<BackgroundSample> samples)
    {
        var inks = new uint?[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            inks[i] = TextRenderStyleSampler.SampleInkArgb(
                bgra, width, height, blocks[i].CoverRect.ToPixelRect(), samples[i].Argb);
        }

        return inks;
    }

    /// <summary>排版结果 → 钉图内容（覆盖块 / 面板 / 状态说明 / 可复制译文）。</summary>
    private static PinContent ToContent(OverlayPlan plan, string? sourceText, BlockTranslation outcome) =>
        new(
            plan.Blocks,
            SourceText: sourceText,
            TranslatedText: plan.TranslatedText,
            StatusMessage: plan.StatusMessage ?? DescribeFailure(outcome))
        {
            CanRetry = outcome.Failed || (outcome.Error is not null && !outcome.HasTranslation),
            PanelText = plan.PanelText,
            PanelHeightDip = plan.PanelHeightDip,
        };

    /// <summary>按块翻译（14.3.2）：≤6 段逐段**并发**；&gt;6 段按字符预算分组合并请求，切分段数对不上即降级整块排版。</summary>
    private async Task<BlockTranslation> TranslateBlocksAsync(
        IReadOnlyList<OcrBlock> blocks, OcrLanguageStatus status, CancellationToken cancellationToken)
    {
        if (blocks.Count == 0)
        {
            return BlockTranslation.None(0);
        }

        if (!IsTranslatable(status))
        {
            return BlockTranslation.None(blocks.Count);
        }

        var engine = _catalog.Resolve(_settings.Engine);
        var source = status.TranslationCode ?? TranslationLanguages.AutoCode;
        var target = _settings.TargetLanguage;

        if (blocks.Count <= OcrBlockGrouping.MaxBlocksPerRequest)
        {
            var tasks = blocks
                .Select(block => TranslateOneAsync(engine, block.SourceText, source, target, cancellationToken))
                .ToArray();
            var results = await Task.WhenAll(tasks);
            var texts = results.Select(result => result.Text).ToArray();
            TranslationErrorType? error = null;
            foreach (var result in results)
            {
                if (result.Error is { } type)
                {
                    error = type;
                    break;
                }
            }

            Log.Information(
                "钉图逐段翻译完成：{Count} 段（成功 {Success} 段，引擎 {Engine}）",
                blocks.Count, texts.Count(text => !string.IsNullOrEmpty(text)), engine.Name);

            return new BlockTranslation(texts, null, false, texts.All(string.IsNullOrEmpty), error);
        }

        // 段落数 > 6：按**字符预算**把连续段落分组，每组一次请求（段落之间空行分隔），返回后切分；
        // 切不上就降级整块排版（不做猜测式对齐）。分组的必要性：密排整屏（最多 60 段）若拼成一次请求，
        // 长度无上限——非官方 Google 走 GET 会撞 URL 长度限制，LLM 引擎会撞 token 上限。
        return await TranslateMergedAsync(blocks, engine, source, target, cancellationToken);
    }

    /// <summary>
    /// 合并请求的字符预算（14.3.2 / 14.3.5）：取 900 是为了与目录里**最小的引擎内部切块长度**
    /// （<c>BingTranslator</c> 非官方接口的 900）对齐——组内文本不会被引擎再切一次，
    /// 返回的段数因此能对上原文段数；对不上仍按既有逻辑降级为整块排版。
    /// </summary>
    private const int MergedRequestCharBudget = 900;

    /// <summary>
    /// 分组合并翻译：**保持段序**逐组请求，每组的译文按空行/换行切回该组的段数。
    /// 任一组切分失败即整体降级（<c>MergedText</c> 取各组原文结果拼接，供整块排版使用）；
    /// 单段独占一组时退回逐段翻译（合并请求对单段没有意义，还会因译文里的换行切错）。
    /// </summary>
    private async Task<BlockTranslation> TranslateMergedAsync(
        IReadOnlyList<OcrBlock> blocks, ITranslator engine, string source, string target,
        CancellationToken cancellationToken)
    {
        var groups = PackBlocks(blocks, MergedRequestCharBudget);
        var texts = new string?[blocks.Count];
        var raw = new List<string>(groups.Count);
        TranslationErrorType? error = null;
        var started = DateTime.UtcNow;

        try
        {
            foreach (var (start, count) in groups)
            {
                if (count == 1)
                {
                    var (text, oneError) = await TranslateOneAsync(
                        engine, blocks[start].SourceText, source, target, cancellationToken);
                    texts[start] = text;
                    error ??= oneError;
                    raw.Add(text ?? string.Empty);
                    continue;
                }

                var mergedSource = string.Join("\n\n", blocks.Skip(start).Take(count).Select(b => b.SourceText));
                var result = await engine.TranslateAsync(mergedSource, source, target, cancellationToken);
                var parts = SplitMerged(result.TranslatedText, count);
                if (parts is null)
                {
                    Log.Information(
                        "合并翻译的切分段数与原文段数不一致，降级为整块排版（{Count} 段，分 {Groups} 组）",
                        blocks.Count, groups.Count);
                    return new BlockTranslation([], string.Join("\n\n", raw.Append(result.TranslatedText)), true, false, null);
                }

                for (var i = 0; i < count; i++)
                {
                    texts[start + i] = parts[i];
                }

                raw.Add(result.TranslatedText);
            }
        }
        catch (Exception ex)
        {
            var failure = DescribeError(ex);
            Log.Warning("钉图合并翻译失败（引擎={Engine}，错误={Error}）：{Reason}", engine.Name, failure, ex.Message);
            return new BlockTranslation([], null, false, true, failure);
        }

        Log.Information(
            "钉图合并翻译完成：{Count} 段（分 {Groups} 组，耗时 {Elapsed} ms，引擎 {Engine}）",
            blocks.Count, groups.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds, engine.Name);
        return new BlockTranslation(texts, null, false, texts.All(string.IsNullOrEmpty), error);
    }

    /// <summary>
    /// 把连续段落按字符预算分组（保持段序，不重排、不跨段拆分）：
    /// 组内以空行拼接后的长度不超过 <paramref name="budget"/>；单段自身超预算时独占一组。
    /// 返回值是 <c>(起始下标, 段数)</c> 列表，因此「段落 → 译文」的对应关系不受分组影响。
    /// </summary>
    internal static IReadOnlyList<(int Start, int Count)> PackBlocks(IReadOnlyList<OcrBlock> blocks, int budget)
    {
        var groups = new List<(int Start, int Count)>();
        if (blocks.Count == 0)
        {
            return groups;
        }

        var limit = Math.Max(1, budget);
        var start = 0;
        var length = blocks[0].SourceText?.Length ?? 0;

        for (var i = 1; i < blocks.Count; i++)
        {
            var textLength = blocks[i].SourceText?.Length ?? 0;
            var withSeparator = length + 2 + textLength; // "\n\n" 的两个字符也要计入
            if (withSeparator > limit)
            {
                groups.Add((start, i - start));
                start = i;
                length = textLength;
            }
            else
            {
                length = withSeparator;
            }
        }

        groups.Add((start, blocks.Count - start));
        return groups;
    }

    private static async Task<(string? Text, TranslationErrorType? Error)> TranslateOneAsync(
        ITranslator engine, string text, string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await engine.TranslateAsync(text, source, target, cancellationToken);
            return (result.TranslatedText, null);
        }
        catch (Exception ex)
        {
            // 单段失败不影响其它段（并发聚合的同一范式）：该段留空，由降级/重试兜底
            return (null, DescribeError(ex));
        }
    }

    /// <summary>合并请求的返回切分：先按空行、再按单换行；段数对不上返回 null（→ 降级整块排版）。</summary>
    internal static IReadOnlyList<string?>? SplitMerged(string? translated, int expected)
    {
        if (string.IsNullOrWhiteSpace(translated) || expected <= 0)
        {
            return null;
        }

        foreach (var separator in new[] { "\n\n", "\r\n\r\n", "\n", "\r\n" })
        {
            var parts = translated
                .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => part.Length > 0)
                .ToArray();
            if (parts.Length == expected)
            {
                return parts;
            }
        }

        return null;
    }

    /// <summary>「识别语言与目标语言相同」跳过态的唯一文案（工具条状态条展示；③-C 强制翻译入口据此识别该状态）。</summary>
    public const string LanguageSkippedReason =
        "识别语言与目标语言相同，已跳过翻译（点工具条「翻译」可强制翻译）";

    /// <summary>非原位替换的三种情形（14.3.1 第 5 条 + 14.3.2）：倾斜、竖排、识别语言 = 目标语言。</summary>
    private string? ResolveForcedSidePanelReason(
        OcrLayout layout, IReadOnlyList<OcrBlock> blocks, OcrLanguageStatus status)
    {
        if (OcrLayoutRules.IsTilted(layout.TextAngle))
        {
            return OverlayLayout.SidePanelTilted;
        }

        if (OcrLayoutRules.HasVerticalText(layout.Lines))
        {
            return OverlayLayout.SidePanelVertical;
        }

        if (!IsTranslatable(status))
        {
            return LanguageSkippedReason;
        }

        return blocks.Count == 0 ? OverlayLayout.SidePanelNoBlocks : null;
    }

    /// <summary>是否需要调用翻译引擎（识别语言 = 目标语言时跳过，13.2.5 规则 2）。</summary>
    private bool IsTranslatable(OcrLanguageStatus status) =>
        !status.ShouldSkipTranslation(_settings.TargetLanguage);

    private static string? DescribeFailure(BlockTranslation outcome) =>
        outcome.Error is { } error
            ? $"翻译失败：{ViewModels.QuickTranslateViewModel.DescribeError(error)}" + (outcome.Failed ? "（可点重试）" : "")
            : null;

    private static TranslationErrorType DescribeError(Exception ex) => ex switch
    {
        TranslationException translation => translation.ErrorType,
        OperationCanceledException => TranslationErrorType.Engine,
        _ => TranslationErrorType.Engine,
    };

    /// <summary>钉图所在屏的缩放（字号下限按 DIP 折算的依据；取不到按 100%）。</summary>
    private static double ResolveDpiScale(PixelRect imageRect, NativeMethods.MONITORINFO monitorInfo)
    {
        var centerX = monitorInfo.RcMonitor.Left + imageRect.X + (imageRect.Width / 2);
        var centerY = monitorInfo.RcMonitor.Top + imageRect.Y + (imageRect.Height / 2);
        return ScreenInterop.TryGetMonitorAt(centerX, centerY, out _, out var scale) && scale > 0 ? scale : 1.0;
    }

    /// <summary>
    /// 文本测量回调（排版字号自适应的唯一依据）：用 WPF 的 <see cref="FormattedText"/> 在**图像像素空间**
    /// 量宽高（`pixelsPerDip = 1`），与钉图层「1 图像像素 = 1 物理像素」的语义一致。
    /// </summary>
    private static (double Width, double Height) MeasureText(string text, double fontSizePx, double maxWidthPx)
    {
        if (string.IsNullOrEmpty(text) || fontSizePx <= 0)
        {
            return (0, 0);
        }

        try
        {
            var family = Application.Current?.TryFindResource("Font.App") as FontFamily
                ?? new FontFamily("Segoe UI");
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                fontSizePx,
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = Math.Max(1, maxWidthPx),
                MaxTextHeight = 100000,
            };

            return (Math.Min(formatted.Width, Math.Max(1, maxWidthPx)), formatted.Height);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "文本测量失败，退回按字符数估算");
            return (Math.Min(text.Length * fontSizePx * 0.6, Math.Max(1, maxWidthPx)),
                Math.Ceiling(text.Length * 0.6 / Math.Max(1, maxWidthPx / (fontSizePx * 0.6))) * fontSizePx * 1.4);
        }
    }

    /// <summary>鼠标位置 → 所在显示器（物理像素）；跨屏框选不支持，只取鼠标所在单屏（13.2.3 已知限制）。</summary>
    private static bool TryResolveMonitor(out IntPtr monitor, out NativeMethods.MONITORINFO info)
    {
        info = default;
        monitor = IntPtr.Zero;

        var cursor = new NativeMethods.POINT();
        if (!NativeMethods.GetPhysicalCursorPos(ref cursor))
        {
            Log.Warning("GetPhysicalCursorPos 失败，无法确定鼠标位置");
            return false;
        }

        monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            Log.Warning("MonitorFromPoint 失败，无法确定鼠标所在显示器");
            return false;
        }

        info = new NativeMethods.MONITORINFO
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            Log.Warning("GetMonitorInfoW 失败，无法读取显示器矩形");
            return false;
        }

        Log.Debug("截图目标显示器：({Left},{Top})-({Right},{Bottom})",
            info.RcMonitor.Left, info.RcMonitor.Top, info.RcMonitor.Right, info.RcMonitor.Bottom);
        return true;
    }

    /// <summary>取消时把焦点还给呼出前的应用（AC 3）。</summary>
    private static void RestoreForeground(IntPtr previous)
    {
        if (previous == IntPtr.Zero || !NativeMethods.IsWindow(previous))
        {
            return;
        }

        try
        {
            NativeMethods.SetForegroundWindow(previous);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "还原截图前的前台窗口失败");
        }
    }

    /// <summary>钉图内容 + 三个回调（重试 / 在小窗中打开 / 强制翻译）。</summary>
    private sealed record PinnedPlan(
        PinContent Content, Func<Task<PinContent>>? RetryAsync, Action? OpenInQuickWindow,
        Func<Task<PinContent>>? ForceTranslateAsync = null);

    /// <summary>逐块翻译的聚合结果。</summary>
    private sealed record BlockTranslation(
        IReadOnlyList<string?> Texts,
        string? MergedText,
        bool SegmentMismatch,
        bool Failed,
        TranslationErrorType? Error)
    {
        /// <summary>有任意一段拿到译文。</summary>
        public bool HasTranslation => Texts.Any(text => !string.IsNullOrEmpty(text))
            || !string.IsNullOrEmpty(MergedText);

        /// <summary>完全没有译文（不翻译的情形，如语言相同）。</summary>
        public static BlockTranslation None(int count) => new(new string?[count], null, false, false, null);
    }
}
