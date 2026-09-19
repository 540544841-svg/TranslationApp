namespace TranslationApp.Core.Capture;

/// <summary>系统可用的一种 OCR 识别语言（来自 OcrEngine.AvailableRecognizerLanguages）。</summary>
public sealed record OcrLanguageTag(string Tag, string DisplayName);

/// <summary>
/// OCR 识别语言与翻译源语言的配合决策结果（13.2.5）。
/// </summary>
/// <param name="IsAvailable">系统是否安装了至少一种 OCR 语言包。</param>
/// <param name="SelectedTag">实际使用的 OCR 语言标签；<see cref="OcrLanguages.Auto"/> 表示跟随系统语言偏好。</param>
/// <param name="IsFallback">用户显式指定的语言不在可用列表中，已回退。</param>
/// <param name="TranslationCode">
/// 传给翻译引擎的源语言码；null = 保持「自动检测」（OCR 语言为 auto，或语言码无法映射到现有语言列表）。
/// </param>
/// <param name="Available">系统可用语言（回退提示与设置页下拉用）。</param>
public sealed record OcrLanguageStatus(
    bool IsAvailable,
    string SelectedTag,
    bool IsFallback,
    string? TranslationCode,
    IReadOnlyList<OcrLanguageTag> Available)
{
    /// <summary>识别语言与目标语言相同时应跳过翻译（13.2.5 规则 2）。</summary>
    public bool ShouldSkipTranslation(string? targetLanguageCode) =>
        TranslationCode is not null
        && targetLanguageCode is not null
        && string.Equals(TranslationCode, targetLanguageCode, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// OCR 语言码与翻译引擎语言码的映射，以及语言包缺失/回退的判定（13.2.5）。
/// 纯逻辑，放 Core 以便单元测试；WinRT 引擎选择留在 App 层的 OcrService。
/// </summary>
public static class OcrLanguages
{
    /// <summary>设置值：跟随系统语言偏好（13.7 默认值）。</summary>
    public const string Auto = "auto";

    /// <summary>
    /// 语言包缺失时的统一文案（13.2.5）：设置页错误条与托盘气泡共用同一份，
    /// 保证「热键不静默失效」时用户看到的指引完全一致。
    /// </summary>
    public const string MissingPackMessage =
        "系统未安装 OCR 识别语言包，截图翻译不可用。请在「设置 → 时间和语言 → 语言和区域」中为需要的语言添加语言包"
        + "（添加语言 → 可选功能：光学字符识别），完成后重启本程序。";

    /// <summary>
    /// Windows OCR 语言标签（BCP-47，如 zh-Hans-CN / en-US / ja）→ 内部翻译语言码（= Google 码）。
    /// 返回 null 表示不强制源语言（保持自动检测）：要么语言不认识，
    /// 要么对应码不在本程序的源语言列表里（如繁体中文 zh-Hant），强行写入会让小窗语言下拉显示空白。
    /// </summary>
    public static string? TryMapToTranslationCode(string? ocrTag)
    {
        if (string.IsNullOrWhiteSpace(ocrTag) || string.Equals(ocrTag, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = ocrTag.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var language = parts[0].ToLowerInvariant();
        var script = parts.Length > 1 && parts[1].Length == 4 ? parts[1].ToLowerInvariant() : null;

        return language switch
        {
            // 繁体中文（zh-Hant）不在本程序语言列表内，交回引擎自动检测更稳
            "zh" => script == "hant" ? null : "zh-CN",
            "en" => "en",
            "ja" => "ja",
            "ko" => "ko",
            "fr" => "fr",
            "de" => "de",
            "ru" => "ru",
            "es" => "es",
            _ => null,
        };
    }

    /// <summary>
    /// 依据设置值与系统可用语言决定实际 OCR 语言（13.2.5）：
    /// auto 直接跟随系统偏好；显式指定但系统没有该语言包时回退到 auto 并标记 IsFallback。
    /// </summary>
    public static OcrLanguageStatus Resolve(string? setting, IReadOnlyList<OcrLanguageTag> available)
    {
        available ??= [];

        if (available.Count == 0)
        {
            return new OcrLanguageStatus(false, Auto, false, null, available);
        }

        if (string.IsNullOrWhiteSpace(setting) || string.Equals(setting, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return new OcrLanguageStatus(true, Auto, false, null, available);
        }

        var matched = available.Any(tag => string.Equals(tag.Tag, setting, StringComparison.OrdinalIgnoreCase));
        return matched
            ? new OcrLanguageStatus(true, setting, false, TryMapToTranslationCode(setting), available)
            : new OcrLanguageStatus(true, Auto, true, null, available);
    }

    /// <summary>脚本对账的冲突阈值：<see cref="OcrScriptScoring.Score"/> 超过它即判定「文本实为该方向脚本」。</summary>
    public const double ScriptConflictThreshold = 0.5;

    /// <summary>
    /// OCR 映射结果与识别文本的**脚本对账**（v1.2 修复批 ③-B）：
    /// 映射结果为 zh/ja/ko 而 <see cref="OcrScriptScoring.Score"/> 判定文本实为拉丁（得分 &gt; 0.5）时返回 null
    /// （保持引擎自动检测、**不触发「识别语言 = 目标语言」跳过**）——防 zh 引擎把英文识别成 CJK 乱码后被误跳过；
    /// 反向同理（映射为拉丁语言而文本实为 CJK，得分 &lt; −0.5 → 返回 null，防 en 引擎把中文识别成拉丁碎片后被强制送英→?）。
    /// 得分在中性区间（含纯数字 / 空文本）时维持映射结果：对账只纠正**方向性冲突**，不做二次猜测。
    /// </summary>
    public static string? ReconcileWithScript(string? mappedCode, string? text)
    {
        if (string.IsNullOrWhiteSpace(mappedCode))
        {
            return mappedCode;
        }

        var score = OcrScriptScoring.Score(text);
        var mappedIsCjk = mappedCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(mappedCode, "ja", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(mappedCode, "ko", StringComparison.OrdinalIgnoreCase);

        if (mappedIsCjk && score > ScriptConflictThreshold)
        {
            return null; // 映射为 CJK 而文本实为拉丁
        }

        if (!mappedIsCjk && score < -ScriptConflictThreshold)
        {
            return null; // 映射为拉丁（等）而文本实为 CJK
        }

        return mappedCode;
    }

    /// <summary>回退提示文案（13.2.5：运行时回退到系统首选语言并在小窗状态行说明）。</summary>
    public static string DescribeFallback(IReadOnlyList<OcrLanguageTag> available) =>
        available.Count == 0
            ? MissingPackMessage
            : $"指定的 OCR 识别语言不可用，已回退到系统首选语言（{available[0].DisplayName}）。"
              + $"可用语言：{string.Join("、", available.Select(tag => tag.DisplayName))}";
}
