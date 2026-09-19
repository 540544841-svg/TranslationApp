using Serilog;
using TranslationApp.Core.Capture;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TranslationApp.Services;

/// <summary>
/// windows 本地 OCR 引擎（FR-030 / C1 引擎抽象）：Windows.Media.Ocr，随系统提供，零 NuGet 依赖。
/// 逻辑自 <c>OcrService</c> 原样迁入（FR-021 / 13.2.2 + C-⑥）：**auto 双跑择优与几何覆盖率门控
/// 留在该实现内部**（14.9.2——paddle 不参与双跑），默认路径行为与 FR-030 之前逐字节一致。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly IReadOnlyList<OcrLanguageTag> _availableLanguages;

    public WindowsOcrEngine(IReadOnlyList<OcrLanguageTag> availableLanguages)
    {
        _availableLanguages = availableLanguages;
    }

    /// <summary>
    /// 识别 BGRA32 缓冲并**同时取回文字位置信息**（FR-027 / 14.3.1）：词框 → 行框并集 + <c>TextAngle</c>。
    /// 坐标属于传入的这张位图（实测确认），若识别前缩小过则由调用方按 `1/Ratio` 还原
    /// （用 <see cref="OcrLayoutRules.Create"/>）。
    /// 返回 null 表示创建不出引擎（语言包缺失）；线条可能为空（画面里没有字）。
    /// <para>C-⑥：语言为 auto 时走**双跑择优**（见 <see cref="RecognizeAutoAsync"/>）。</para>
    /// </summary>
    public async Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
    {
        if (bgra.Length < width * height * BgraImage.BytesPerPixel || width <= 0 || height <= 0)
        {
            Log.Warning("OCR 输入缓冲尺寸不匹配：{Length} 字节，{Width}x{Height}", bgra.Length, width, height);
            return null;
        }

        var isAuto = string.IsNullOrWhiteSpace(languageTag)
                     || string.Equals(languageTag, OcrLanguages.Auto, StringComparison.OrdinalIgnoreCase);

        OcrResult? result;
        string? engineTag;
        if (isAuto)
        {
            (result, engineTag) = await RecognizeAutoAsync(bgra, width, height);
        }
        else
        {
            var engine = CreateEngine(languageTag);
            if (engine is null)
            {
                Log.Warning("创建 OCR 引擎失败（语言={Language}），系统可能未安装对应语言包", languageTag);
                return null;
            }

            (result, engineTag) = await RecognizeWithEngineAsync(engine, bgra, width, height);
        }

        return result is null ? null : BuildRecognition(result, engineTag);
    }

    /// <summary>
    /// auto 双跑择优（C-⑥）：配置文件语言引擎与 en-US 引擎（若已安装且不同）各识别一次，
    /// 按 <see cref="OcrScriptScoring"/> 的脚本合理性打分选优。修复的缺陷链路：
    /// 中文系统上英文文本被 zh 引擎识别成 CJK 乱码 → 以 auto 送翻译 → 引擎检测为 zh → zh→zh 近直通 →
    /// 乱码被当译文原位显示。双跑只多一次识别（百毫秒级）。
    /// <para>覆盖率门控用**词框总面积**（几何量）：zh 乱码的字符数往往比英文原文更长，
    /// 字符数比较会骗过门控；两个引擎看同一张图，识别正确时词框总面积必然接近。</para>
    /// </summary>
    private async Task<(OcrResult? Result, string? EngineTag)> RecognizeAutoAsync(
        byte[] bgra, int width, int height)
    {
        var profile = CreateProfileEngine();
        if (profile is null)
        {
            Log.Warning("创建 OCR 引擎失败（auto），系统可能未安装语言包");
            return (null, null);
        }

        var profileTag = profile.RecognizerLanguage.LanguageTag;
        var en = TryCreateFromTag("en-US");
        var enTag = en?.RecognizerLanguage.LanguageTag;
        if (en is null || string.Equals(enTag, profileTag, StringComparison.OrdinalIgnoreCase))
        {
            // en-US 未安装或与配置文件语言引擎相同：单跑即可，无可择优
            return await RecognizeWithEngineAsync(profile, bgra, width, height);
        }

        var (primary, _) = await RecognizeWithEngineAsync(profile, bgra, width, height);
        if (primary is null)
        {
            return (null, null);
        }

        var (secondary, secondaryTag) = await RecognizeWithEngineAsync(en, bgra, width, height);
        var primaryArea = TotalWordBoxArea(primary);
        var secondaryArea = TotalWordBoxArea(secondary);
        if (secondary is null
            || !OcrScriptScoring.ShouldPreferSecondary(primary.Text, secondary.Text, secondaryArea, primaryArea))
        {
            // 决策日志从 Debug 提到 Information：只含得分 / 内容字符数 / 词框面积与命中条件，无识别内容（需求 6 脱敏）
            Log.Information(
                "auto 双跑择优：保持配置文件语言引擎 {Tag}（profile 得分 {P:0.###}、内容 {PC} 字、词框 {PA:0} px²；"
                + "en 得分 {S:0.###}、内容 {SC} 字、词框 {SA:0} px²；{Reason}）",
                profileTag,
                OcrScriptScoring.Score(primary.Text), OcrScriptScoring.ContentChars(primary.Text), primaryArea,
                OcrScriptScoring.Score(secondary?.Text), OcrScriptScoring.ContentChars(secondary?.Text), secondaryArea,
                secondary is null ? "en 引擎识别失败" : OcrScriptScoring.DescribeDecision(primary.Text, secondary.Text, secondaryArea, primaryArea));
            return (primary, profileTag);
        }

        // 只记打分元数据，不记录识别内容（需求 6 日志脱敏）；逃生口生效时补标记便于线上归因（14.3.12.8）
        var decision = OcrScriptScoring.DescribeDecision(primary.Text, secondary.Text, secondaryArea, primaryArea);
        Log.Information(
            "auto 双跑择优：en-US 引擎胜出{Escape}（en 得分 {S:0.###}、词框 {SA:0} px² > profile 得分 {P:0.###}、词框 {PA:0} px²），本次按 en-US 识别",
            decision.Contains("逃生口", StringComparison.Ordinal) ? "（脚本冲突逃生口）" : "",
            OcrScriptScoring.Score(secondary.Text), secondaryArea,
            OcrScriptScoring.Score(primary.Text), primaryArea);
        return (secondary, secondaryTag ?? "en-US");
    }

    /// <summary>识别结果的词框总面积（Σ word.BoundingRect 面积，图像像素²）：几何覆盖率门控的输入。</summary>
    private static double TotalWordBoxArea(OcrResult? result)
    {
        if (result is null)
        {
            return 0;
        }

        double total = 0;
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    total += rect.Width * rect.Height;
                }
            }
        }

        return total;
    }

    /// <summary>识别结果 → <see cref="OcrRecognition"/>（词框 → 行框并集 + 文本 + 引擎语言）。</summary>
    private static OcrRecognition BuildRecognition(OcrResult result, string? engineTag)
    {
        var lines = new List<OcrLineBox>(result.Lines.Count);
        var texts = new List<string?>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            texts.Add(line.Text);

            // OcrLine 没有 BoundingRect：行框一律由该行词框并集算出（14.3.1 明确点名的坑）
            var words = line.Words
                .Select(word => new OcrWordBox(
                    word.Text,
                    new OcrRect(
                        word.BoundingRect.Left, word.BoundingRect.Top,
                        word.BoundingRect.Right, word.BoundingRect.Bottom)))
                .ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            lines.Add(new OcrLineBox(
                lines.Count, line.Text, words, OcrRect.Union(words.Select(word => word.Rect))));
        }

        return new OcrRecognition(
            OcrTextComposer.Compose(texts), lines, result.TextAngle, engineTag);
    }

    /// <summary>
    /// 识别 BGRA32 缓冲。缓冲的 alpha 通道不参与识别（BitmapAlphaMode.Ignore）：
    /// GDI 截屏得到的 alpha 常为 0，用 Premultiplied 会被当成全透明图。
    /// 返回 null 表示创建不出引擎（语言包缺失）；文本可能为空串（画面里没有字）。
    /// </summary>
    private static async Task<(OcrResult? Result, string? EngineTag)> RecognizeWithEngineAsync(
        OcrEngine engine, byte[] bgra, int width, int height)
    {
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(bgra);
            var buffer = writer.DetachBuffer();
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);

            return (await engine.RecognizeAsync(bitmap), engine.RecognizerLanguage.LanguageTag);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR 识别失败（{Width}x{Height}）", width, height);
            return (null, null);
        }
    }

    /// <summary>按指定语言建引擎；指定不可用或为 auto 时回退系统首选语言，再退首个可用语言。</summary>
    private OcrEngine? CreateEngine(string? languageTag)
    {
        if (!string.IsNullOrWhiteSpace(languageTag)
            && !string.Equals(languageTag, OcrLanguages.Auto, StringComparison.OrdinalIgnoreCase))
        {
            var engine = TryCreateFromTag(languageTag);
            if (engine is not null)
            {
                return engine;
            }

            Log.Warning("指定 OCR 语言不可用（{Language}），回退系统首选语言", languageTag);
        }

        return CreateProfileEngine();
    }

    /// <summary>配置文件语言引擎（auto 的主候选）：系统语言偏好，再退首个可用语言。</summary>
    private OcrEngine? CreateProfileEngine()
    {
        try
        {
            var profile = OcrEngine.TryCreateFromUserProfileLanguages();
            if (profile is not null)
            {
                return profile;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "按系统语言偏好创建 OCR 引擎失败，改用可用语言列表");
        }

        foreach (var language in _availableLanguages)
        {
            var engine = TryCreateFromTag(language.Tag);
            if (engine is not null)
            {
                return engine;
            }
        }

        return null;
    }

    private static OcrEngine? TryCreateFromTag(string languageTag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OCR 语言标签非法：{Language}", languageTag);
            return null;
        }
    }
}
