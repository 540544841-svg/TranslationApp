using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// FR-029-1 预处理增强（14.3.12.4）单测：触发条件边界、scale 钳制、ratio 复合、
/// 增强算子（双线性/对比度拉伸）、双跑择优集成、AC 1 零回归与 AC 3 故障隔离注入。
/// </summary>
public class OcrPreprocessTests
{
    // ==================== 测试素材 ====================

    private static OcrLineBox Line(double x, double y, double width, double height, string text = "word") =>
        new(0, text,
            [new OcrWordBox(text, new OcrRect(x, y, x + width, y + height))],
            new OcrRect(x, y, x + width, y + height));

    private static OcrPreprocessCandidate Candidate(string? text, params OcrLineBox[] lines) => new(text, lines);

    private static byte[] Buffer(int width, int height, byte gray)
    {
        var buffer = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            buffer[i * 4] = gray;
            buffer[i * 4 + 1] = gray;
            buffer[i * 4 + 2] = gray;
            buffer[i * 4 + 3] = 255;
        }

        return buffer;
    }

    /// <summary>auto 模式下的「清晰大字」主候选（不会命中任何触发条件）。</summary>
    private static OcrPreprocessCandidate BigClearCandidate() => new(
        "abcdef ghij",
        [Line(0, 0, 60, 20, "abcdef"), Line(0, 30, 60, 20, "ghij")]);

    // ==================== 三态归一化 ====================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("on", "on")]
    [InlineData("off", "off")]
    [InlineData("ON", "on")]
    [InlineData(" Off ", "off")]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("unknown", "auto")]
    public void NormalizeMode_UnknownFallsBackToAuto(string? input, string expected) =>
        Assert.Equal(expected, OcrPreprocess.NormalizeMode(input));

    // ==================== 触发条件（AC 2 边界） ====================

    [Fact]
    public void ShouldTrigger_MedianExactly14_NotTriggered()
    {
        // 行高 = 14（不小于阈值）、内容 6 字（不小于阈值）、面积占比 0.07（不低于阈值）
        var candidate = Candidate("abcdef", Line(0, 0, 100, OcrPreprocess.SmallLineHeightPx));
        var triggered = OcrPreprocess.ShouldTrigger(candidate, 200, 100, out var reason);

        Assert.False(triggered);
        Assert.Contains("行高中位数 14", reason);
    }

    [Fact]
    public void ShouldTrigger_SmallLineHeight_TriggersWithReason()
    {
        var candidate = Candidate("abcdef", Line(0, 0, 100, 13.9));
        var triggered = OcrPreprocess.ShouldTrigger(candidate, 200, 100, out var reason);

        Assert.True(triggered);
        Assert.Contains("小字", reason);
    }

    [Fact]
    public void ShouldTrigger_LowContentAndTinyArea_Triggers()
    {
        // 内容 3 字（< 6）且词框占比 2.8%（< 5%）
        var candidate = Candidate("abcde", Line(0, 0, 40, 14));
        var triggered = OcrPreprocess.ShouldTrigger(candidate, 200, 100, out var reason);

        Assert.True(triggered);
        Assert.Contains("零碎漏识", reason);
    }

    [Fact]
    public void ShouldTrigger_EmptyRecognition_Triggers()
    {
        // 空画面识别（0 行 0 字）：触发②（词框占比 0%），增强重跑有机会找回漏识文字
        var triggered = OcrPreprocess.ShouldTrigger(Candidate(null), 200, 100, out _);

        Assert.True(triggered);
    }

    [Fact]
    public void ShouldTrigger_LowContentButLargeArea_NotTriggered()
    {
        // 词框占比 50%：选区大部分墨迹已框出，不算零碎漏识
        var candidate = Candidate("abcde", Line(0, 0, 100, 100));
        Assert.False(OcrPreprocess.ShouldTrigger(candidate, 200, 100, out _));
    }

    [Fact]
    public void ShouldTrigger_TinyAreaButEnoughChars_NotTriggered()
    {
        // 内容 10 字 ≥ 6：条件②不成立（缺词框占比与内容量任一都不触发）
        var candidate = Candidate("abcdefghijkl", Line(0, 0, 40, 14));
        Assert.False(OcrPreprocess.ShouldTrigger(candidate, 200, 100, out _));
    }

    // ==================== scale 钳制与还原比例复合 ====================

    [Theory]
    [InlineData(500, 500, 2.0)]          // 小选区 → 上限 2.0
    [InlineData(1000, 1000, 2.0)]        // 恰好 2.0
    [InlineData(2000, 2000, 1.0)]        // 已达 4 MP → 不放大
    [InlineData(3000, 3000, 1.0)]        // 超上限 → 不缩小（下限钳到 1，只做对比度拉伸）
    [InlineData(0, 100, 1.0)]            // 非法输入 → 恒等
    [InlineData(1920, 1080, 1.389)]      // sqrt(4e6 / 2073600)
    public void ComputeScale_ClampsToAllowedRange(int width, int height, double expected) =>
        Assert.Equal(expected, OcrPreprocess.ComputeScale(width, height), 3);

    [Fact]
    public void ComputeScale_CustomCapRespected() =>
        Assert.Equal(1.0, OcrPreprocess.ComputeScale(2000, 2000, 1_000_000), 3);

    [Theory]
    [InlineData(0.8, 2.0, 0.4)]
    [InlineData(1.25, 2.0, 0.625)]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(0.0, 2.0, 0.5)]   // 非法 fitRatio 先归一为 1
    [InlineData(0.8, 0.0, 0.8)]   // 非法 scale 直接返回 fitRatio
    public void CombineRestoreRatio_ComposesFitAndScale(double fitRatio, double scale, double expected) =>
        Assert.Equal(expected, OcrPreprocess.CombineRestoreRatio(fitRatio, scale), 9);

    // ==================== 双线性重采样 ====================

    [Fact]
    public void ResizeBilinear_InterpolatesBetweenSourcePixels()
    {
        // 2x2 棋盘（B 通道）：左上 0、右上 255、左下 255、右下 0 → 放大到 4x4
        var source = new byte[2 * 2 * 4];
        void Set(int x, int y, byte b)
        {
            source[(y * 2 + x) * 4] = b;
            source[(y * 2 + x) * 4 + 3] = 255;
        }

        Set(0, 0, 0);
        Set(1, 0, 255);
        Set(0, 1, 255);
        Set(1, 1, 0);

        var result = BgraImage.ResizeBilinear(source, 2, 2, 4, 4);
        Assert.Equal(4 * 4 * 4, result.Length);

        // 角落保持源像素
        Assert.Equal(0, result[0]);
        Assert.Equal(255, result[(0 * 4 + 3) * 4]);
        // 内部点为双线性插值：dest(1,1) = 95.625 → 96
        Assert.Equal(96, result[(1 * 4 + 1) * 4]);
        // alpha 一律 255
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void ResizeBilinear_SameSizeReturnsCopy()
    {
        var source = Buffer(3, 3, 128);
        var result = BgraImage.ResizeBilinear(source, 3, 3, 3, 3);
        Assert.Equal(source, result);
    }

    [Theory]
    [InlineData(2, 2, 4, 4, 8)]   // 源缓冲只有 8 字节 < 2x2x4 → 非法
    public void ResizeBilinear_InvalidInputReturnsEmpty(int width, int height, int newWidth, int newHeight, int bufferLength)
    {
        Assert.Empty(BgraImage.ResizeBilinear(null!, width, height, newWidth, newHeight));
        Assert.Empty(BgraImage.ResizeBilinear(new byte[bufferLength], width, height, newWidth, newHeight));
    }

    // ==================== 增强算子（放大 + 对比度拉伸） ====================

    [Fact]
    public void Enhance_ScalesDimensionsAndClampsScale()
    {
        var buffer = Buffer(100, 80, 30);

        var result2x = OcrPreprocess.Enhance(buffer, 100, 80, 2.0, out var w2, out var h2);
        Assert.Equal((200, 160), (w2, h2));
        Assert.Equal(200 * 160 * 4, result2x!.Length);

        OcrPreprocess.Enhance(buffer, 100, 80, 5.0, out var w5, out _);
        Assert.Equal(200, w5); // 超上限钳到 2.0

        var result1x = OcrPreprocess.Enhance(buffer, 100, 80, 0.5, out var w1, out var h1);
        Assert.Equal((100, 80), (w1, h1)); // 低于 1 钳到 1：只拉伸不缩小
        Assert.Equal(buffer.Length, result1x!.Length);
    }

    [Fact]
    public void Enhance_NeverExceedsPixelCap()
    {
        var scale = OcrPreprocess.ComputeScale(1920, 1080);
        var result = OcrPreprocess.Enhance(Buffer(1920, 1080, 10), 1920, 1080, scale, out var newWidth, out var newHeight);

        Assert.NotNull(result);
        Assert.True((long)newWidth * newHeight <= OcrPreprocess.DefaultMaxPixels);
    }

    [Fact]
    public void Enhance_HighContrastImageIsNearIdentity()
    {
        // 半黑半白：2%/98% 分位 = 0/255 → 映射恒等（AC「对已高对比图像天然安全」）
        var buffer = Buffer(10, 10, 0);
        for (var i = 50; i < 100; i++)
        {
            buffer[i * 4] = 255;
            buffer[i * 4 + 1] = 255;
            buffer[i * 4 + 2] = 255;
        }

        var result = OcrPreprocess.Enhance(buffer, 10, 10, 1.0, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(0, result[0]);
        Assert.Equal(255, result[50 * 4 + 2]);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void Enhance_LowContrastIsStretchedToFullRange()
    {
        // 全图只有亮度 100/110 两档 → 拉伸后 0/255
        var buffer = Buffer(10, 10, 100);
        for (var i = 50; i < 100; i++)
        {
            buffer[i * 4] = 110;
            buffer[i * 4 + 1] = 110;
            buffer[i * 4 + 2] = 110;
        }

        var result = OcrPreprocess.Enhance(buffer, 10, 10, 1.0, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(0, result[0]);
        Assert.Equal(255, result[50 * 4]);
        Assert.Equal(255, result[50 * 4 + 1]);
    }

    [Fact]
    public void Enhance_ConstantImageReturnsUnchanged()
    {
        var buffer = Buffer(10, 10, 128);
        var result = OcrPreprocess.Enhance(buffer, 10, 10, 1.0, out _, out _);

        Assert.NotNull(result);
        Assert.Equal(buffer, result);
    }

    [Fact]
    public void Enhance_InvalidInputReturnsNull()
    {
        Assert.Null(OcrPreprocess.Enhance(null, 10, 10, 2.0, out _, out _));
        Assert.Null(OcrPreprocess.Enhance(new byte[8], 10, 10, 2.0, out _, out _));
    }

    // ==================== 双跑择优集成 ====================

    [Fact]
    public void ShouldUseEnhanced_NormalizesAreaByScaleSquared()
    {
        // 增强候选词框 4000 px²（放大空间）÷ scale²(4) = 1000 ≥ 0.6 × 1500 → 条件②成立；
        // 得分领先（拉丁 0.911 vs CJK -1）→ 增强胜出
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10), Line(0, 15, 50, 10), Line(0, 30, 50, 10));
        var enhanced = Candidate("hello world", Line(0, 0, 100, 20), Line(0, 30, 100, 20));

        Assert.True(OcrPreprocess.ShouldUseEnhanced(primary, enhanced, 2.0));
        // 同一候选若不做归一（面积 4000 直接比）也会胜出——反证归一的必要性见下一个用例
    }

    [Fact]
    public void ShouldUseEnhanced_RejectsWhenNormalizedAreaTooSmall()
    {
        // 增强候选词框 2400 px² ÷ 4 = 600 < 0.6 × 1500 = 900 → 条件②不成立，即使得分领先也不切换
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10), Line(0, 15, 50, 10), Line(0, 30, 50, 10));
        var enhanced = Candidate("hello world", Line(0, 0, 60, 20), Line(0, 30, 60, 20));

        Assert.False(OcrPreprocess.ShouldUseEnhanced(primary, enhanced, 2.0));
    }

    // ==================== 与脚本冲突逃生口联动（14.3.12.8 测试⑥） ====================

    /// <summary>zh 引擎对斜体英文歌词的「拉丁为主、混入 CJK 杂质」乱码（同 OcrScriptScoringTests 的探针摘录形态）。</summary>
    private const string LatinShapedGarbageWithCjk =
        "SOI 1 司 ie over the rainb way 加 刀 once Someday upon 豆 看 where the clo troubles 方 lemon drops " +
        "w above the mey tops a where yo 刀";

    [Fact]
    public void ShouldUseEnhanced_原候选拉丁形乱码混CJK_增强候选纯拉丁成段_逃生口判增强胜出()
    {
        // 原候选得分 ≈ 0.82（> 0.5）且混 ≥ 2 个 CJK（E1）、增强候选纯拉丁成段（E2），
        // 得分领先 ≈ 0.10 < 0.15（条件③不成立）→ 由脚本冲突逃生口豁免，判增强候选胜出
        var primary = Candidate(LatinShapedGarbageWithCjk, Line(0, 0, 300, 25));
        var enhanced = Candidate(
            "Somewhere over the rainbow way up high, There's a land that I heard of once in a lullaby.",
            Line(0, 0, 600, 50), Line(0, 60, 600, 50));

        // 前置自检：领先确在 0.15 以内（命中的是逃生口而非条件③）
        Assert.True(OcrScriptScoring.Score(enhanced.Text) - OcrScriptScoring.Score(primary.Text)
                    <= OcrScriptScoring.Epsilon,
            $"领先 {OcrScriptScoring.Score(enhanced.Text) - OcrScriptScoring.Score(primary.Text):0.###} 应 ≤ 0.15");
        Assert.True(OcrPreprocess.ShouldUseEnhanced(primary, enhanced, 2.0));
    }

    // ==================== 管线：AC 1 零回归 / AC 3 故障隔离 / 择优 ====================

    [Fact]
    public async Task RunAsync_Off_NeverRerunsAndKeepsOriginalPath()
    {
        // AC 1（off）：增强识别回调一次都不调 → 原图识别路径与旧版本完全一致
        var calls = 0;
        var primary = BigClearCandidate();

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 1.0, OcrPreprocess.ModeOff,
            (_, _, _) => { calls++; return Task.FromResult<OcrPreprocessCandidate?>(null); });

        Assert.Equal(0, calls);
        Assert.Equal(OcrPreprocessAction.Disabled, outcome.Action);
        Assert.False(outcome.EnhancedUsed);
        Assert.Equal(primary, outcome.Candidate);
        Assert.Equal(1.0, outcome.RestoreRatio, 9);
        Assert.Contains("仅原图识别", outcome.LogMessage);
    }

    [Fact]
    public async Task RunAsync_AutoNotTriggered_LogsSkipAndNeverReruns()
    {
        // AC 1（auto 未触发）：日志明确显示「未触发预处理」，识别回调零调用（原图路径零回归）
        var calls = 0;
        var primary = BigClearCandidate();

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 1.0, OcrPreprocess.ModeAuto,
            (_, _, _) => { calls++; return Task.FromResult<OcrPreprocessCandidate?>(null); });

        Assert.Equal(0, calls);
        Assert.Equal(OcrPreprocessAction.NotTriggered, outcome.Action);
        Assert.Equal(OcrPreprocessLogLevel.Information, outcome.Level);
        Assert.Contains("未触发预处理", outcome.LogMessage);
        // 日志脱敏：决策日志不含识别内容
        Assert.DoesNotContain("abcdef", outcome.LogMessage);
        Assert.Equal(primary, outcome.Candidate);
        Assert.Equal(1.0, outcome.RestoreRatio, 9);
    }

    [Fact]
    public async Task RunAsync_AutoTriggered_EnhancedWinsWithCompositeRatio()
    {
        // 触发①（行高 10 < 14）→ 重跑一次（传入放大后的尺寸与缓冲）→ 增强候选胜出 → 还原比例 = fitRatio / scale
        var calls = new List<(int Width, int Height, int Bytes)>();
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10), Line(0, 15, 50, 10), Line(0, 30, 50, 10));
        var enhanced = Candidate("hello world", Line(0, 0, 100, 20), Line(0, 30, 100, 20));

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (bgra, width, height) =>
            {
                calls.Add((width, height, bgra.Length));
                return Task.FromResult<OcrPreprocessCandidate?>(enhanced);
            });

        var call = Assert.Single(calls);
        Assert.Equal(400, call.Width);   // 200 × scale 2.0
        Assert.Equal(200, call.Height);  // 100 × scale 2.0
        Assert.Equal(400 * 200 * 4, call.Bytes);
        Assert.True(outcome.EnhancedUsed);
        Assert.Equal(OcrPreprocessAction.EnhancedUsed, outcome.Action);
        Assert.Equal(2.0, outcome.Scale!.Value, 9);
        Assert.Equal(0.4, outcome.RestoreRatio, 9); // 0.8 / 2.0
        Assert.Equal(enhanced, outcome.Candidate);
        Assert.Contains("增强候选胜出", outcome.LogMessage);
        Assert.Contains("全部条件命中", outcome.LogMessage);
        Assert.DoesNotContain("hello", outcome.LogMessage); // 脱敏
    }

    [Fact]
    public async Task RunAsync_AutoTriggered_KeepsPrimaryWhenEnhancedLoses()
    {
        // 触发①但增强候选得分接近（同文本）→ 保持原候选，还原比例不变
        var primary = Candidate("abcdefghij", Line(0, 0, 100, 10));
        var enhanced = Candidate("abcdefghij", Line(0, 0, 200, 20));

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (_, _, _) => Task.FromResult<OcrPreprocessCandidate?>(enhanced));

        Assert.Equal(OcrPreprocessAction.KeptOriginal, outcome.Action);
        Assert.False(outcome.EnhancedUsed);
        Assert.Equal(primary, outcome.Candidate);
        Assert.Equal(0.8, outcome.RestoreRatio, 9);
        Assert.Contains("保持原候选", outcome.LogMessage);
        Assert.Contains("得分接近", outcome.LogMessage);
    }

    [Fact]
    public async Task RunAsync_OnMode_AlwaysRerunsEvenForClearText()
    {
        // on：跳过触发判定，清晰大字也重跑一次（仍受择优约束 → 同文本保持原候选）
        var calls = 0;
        var primary = BigClearCandidate();

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 1.0, OcrPreprocess.ModeOn,
            (bgra, width, height) =>
            {
                calls++;
                return Task.FromResult<OcrPreprocessCandidate?>(
                    new OcrPreprocessCandidate(primary.Text, [Line(0, 0, 120, 40, "abcdef"), Line(0, 60, 120, 40, "ghij")]));
            });

        Assert.Equal(1, calls);
        Assert.Equal(OcrPreprocessAction.KeptOriginal, outcome.Action);
    }

    [Fact]
    public async Task RunAsync_EnhancedThrows_FaultIsolatedKeepsPrimary()
    {
        // AC 3（故障隔离注入）：增强重跑抛异常 → 主链路拿到原候选，仅 Warning，异常被捕获回传
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10));
        var injected = new InvalidOperationException("injected");

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (_, _, _) => Task.FromException<OcrPreprocessCandidate?>(injected));

        Assert.Equal(OcrPreprocessAction.FaultIsolated, outcome.Action);
        Assert.Equal(OcrPreprocessLogLevel.Warning, outcome.Level);
        Assert.Same(injected, outcome.Error);
        Assert.Equal(primary, outcome.Candidate);
        Assert.Equal(0.8, outcome.RestoreRatio, 9);
        Assert.False(outcome.EnhancedUsed);
        Assert.Contains("保持原候选", outcome.LogMessage);
    }

    [Fact]
    public async Task RunAsync_EnhanceThrows_FaultIsolatedKeepsPrimary()
    {
        // AC 3：增强算子本身抛异常（缓冲尺寸在调用后仍可能被篡改）→ 同样隔离
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10));
        var tinyBuffer = new byte[4];

        var outcome = await OcrPreprocess.RunAsync(
            primary, tinyBuffer, 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (_, _, _) => throw new Xunit.Sdk.XunitException("不应重跑"));

        Assert.Equal(OcrPreprocessAction.FaultIsolated, outcome.Action);
        Assert.Equal(OcrPreprocessLogLevel.Warning, outcome.Level);
        Assert.Equal(primary, outcome.Candidate);
    }

    [Fact]
    public async Task RunAsync_EnhancedReturnsNull_KeepsPrimaryWithWarning()
    {
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10));

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (_, _, _) => Task.FromResult<OcrPreprocessCandidate?>(null));

        Assert.Equal(OcrPreprocessAction.NoEnhancedResult, outcome.Action);
        Assert.Equal(OcrPreprocessLogLevel.Warning, outcome.Level);
        Assert.Equal(primary, outcome.Candidate);
    }

    [Fact]
    public async Task RunAsync_EnhancedEmptyLines_KeepsPrimary()
    {
        // 增强重跑 0 行（块数为 0）→ 保持原候选（14.3.12.4 回退安全）
        var primary = Candidate("啊吧嘣", Line(0, 0, 50, 10));

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 0.8, OcrPreprocess.ModeAuto,
            (_, _, _) => Task.FromResult<OcrPreprocessCandidate?>(Candidate(null)));

        Assert.Equal(OcrPreprocessAction.NoEnhancedResult, outcome.Action);
        Assert.Equal(primary, outcome.Candidate);
        Assert.Equal(0.8, outcome.RestoreRatio, 9);
    }

    [Fact]
    public async Task RunAsync_EmptyPrimary_StillTriggersAndCanRecoverText()
    {
        // 原图识别全空（零碎漏识的极端）→ 触发②重跑 → 增强找回文字
        var primary = Candidate(null);
        var enhanced = Candidate("recovered", Line(0, 0, 100, 20, "recovered"));

        var outcome = await OcrPreprocess.RunAsync(
            primary, Buffer(200, 100, 10), 200, 100, 1.0, OcrPreprocess.ModeAuto,
            (_, _, _) => Task.FromResult<OcrPreprocessCandidate?>(enhanced));

        Assert.True(outcome.EnhancedUsed);
        Assert.Equal(enhanced, outcome.Candidate);
        Assert.Equal(0.5, outcome.RestoreRatio, 9); // 1.0 / 2.0
    }

    [Fact]
    public async Task RunAsync_NullPrimary_SkipsPreprocessing()
    {
        // 原图识别已失败（引擎创建不出）→ 不重跑，保持既有失败路径
        var calls = 0;

        var outcome = await OcrPreprocess.RunAsync(
            null, Buffer(200, 100, 10), 200, 100, 1.0, OcrPreprocess.ModeAuto,
            (_, _, _) => { calls++; return Task.FromResult<OcrPreprocessCandidate?>(null); });

        Assert.Equal(0, calls);
        Assert.False(outcome.EnhancedUsed);
        Assert.Equal(OcrPreprocessAction.NotTriggered, outcome.Action);
    }
}
