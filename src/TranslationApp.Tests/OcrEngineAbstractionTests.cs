using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// FR-030 引擎抽象（14.9.2）单测：OcrEngineRouter 双引擎分发与降级（paddle 失败 → windows + 一次性提示）、
/// OcrEngineNames 归一化、PaddleLayoutMapper 四点框→伪词行框（向量取自 C0 探针 D:\ocrprobe\report.md §7
/// 的固定样本输出）、PaddleSessionPolicy 懒加载/空闲释放时序纯逻辑。
/// paddle 真实推理依赖模型与机器，不写自动化测试（C0 探针数据佐证：10 行中位 0.49s、斜体相似度 99.1%）。
/// </summary>
public class OcrEngineAbstractionTests
{
    // ==================== 测试素材 ====================

    /// <summary>恒等引擎：记录调用并回传构造时给定的结果/异常。</summary>
    private sealed class FakeEngine(OcrRecognition? result = null, Exception? throwOnCall = null) : IOcrEngine
    {
        public int Calls { get; private set; }

        public string? LastLanguageTag { get; private set; }

        public Task<OcrRecognition?> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
        {
            Calls++;
            LastLanguageTag = languageTag;
            return throwOnCall is not null
                ? Task.FromException<OcrRecognition?>(throwOnCall)
                : Task.FromResult(result);
        }
    }

    private static OcrRecognition Recognition(string text = "recognized") => new(text, [], null, "en-US");

    /// <summary>探针 S1（印刷体英文 10 行）首行四点框（report.md §7）。</summary>
    private static (double X, double Y)[] ProbeS1FirstQuad =>
        [(17, 11), (385, 13), (385, 43), (17, 41)];

    /// <summary>探针 S4（小字号 UI）首行四点框（report.md §7）。</summary>
    private static (double X, double Y)[] ProbeS4FirstQuad =>
        [(11, 7), (207, 8), (206, 31), (11, 30)];

    // ==================== OcrEngineRouter：分发 ====================

    [Fact]
    public async Task Router_默认设置分发到windows引擎_paddle不被触碰()
    {
        var windows = new FakeEngine(Recognition("from-windows"));
        var paddle = new FakeEngine(Recognition("from-paddle"));
        var router = new OcrEngineRouter(windows, paddle, () => OcrEngineNames.Windows);

        var result = await router.RecognizeAsync([], 1, 1, "auto");

        Assert.Equal("from-windows", result!.Text);
        Assert.Equal(1, windows.Calls);
        Assert.Equal(0, paddle.Calls);
        Assert.False(router.IsPaddleDegraded);
    }

    [Fact]
    public async Task Router_设置paddle时分发到paddle引擎_不执行windows双跑()
    {
        var windows = new FakeEngine(Recognition("from-windows"));
        var paddle = new FakeEngine(Recognition("from-paddle"));
        var router = new OcrEngineRouter(windows, paddle, () => OcrEngineNames.Paddle);

        var result = await router.RecognizeAsync([], 1, 1, "zh-Hans-CN");

        Assert.Equal("from-paddle", result!.Text);
        Assert.Equal(0, windows.Calls); // paddle 是显式选择的独立引擎，不走 zh/en 双跑（14.9.2）
        Assert.Equal(1, paddle.Calls);
        Assert.Equal("zh-Hans-CN", paddle.LastLanguageTag); // 语言标签原样透传（引擎内决定是否忽略）
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("WINDOWS")]
    public async Task Router_设置未知或非paddle值_按windows分发(string? setting)
    {
        var windows = new FakeEngine(Recognition());
        var paddle = new FakeEngine(Recognition());
        var router = new OcrEngineRouter(windows, paddle, () => setting);

        await router.RecognizeAsync([], 1, 1, "auto");

        Assert.Equal(1, windows.Calls);
        Assert.Equal(0, paddle.Calls);
    }

    [Fact]
    public async Task Router_paddle未随包分发时恒走windows()
    {
        var windows = new FakeEngine(Recognition());
        var router = new OcrEngineRouter(windows, null, () => OcrEngineNames.Paddle);

        var result = await router.RecognizeAsync([], 1, 1, "auto");

        Assert.NotNull(result);
        Assert.Equal(1, windows.Calls);
        Assert.False(router.IsPaddleDegraded);
    }

    // ==================== OcrEngineRouter：降级（14.9.2 降级策略①②） ====================

    [Fact]
    public async Task Router_paddle初始化或推理失败_本次及后续回退windows_一次性提示()
    {
        var windows = new FakeEngine(Recognition("from-windows"));
        var injected = new InvalidOperationException("ORT 原生库加载失败（模拟杀软误删）");
        var paddle = new FakeEngine(throwOnCall: injected);
        var notices = new List<string>();
        var failures = new List<Exception>();
        var router = new OcrEngineRouter(
            windows, paddle, () => OcrEngineNames.Paddle,
            notifyFallback: notices.Add, logPaddleFailure: failures.Add);

        var first = await router.RecognizeAsync([], 1, 1, "auto");
        var second = await router.RecognizeAsync([], 1, 1, "auto");

        // 降级不改变本次结果：第一次 = windows 兜底结果；提示与日志各恰好一次（不反复弹泡）
        Assert.Equal("from-windows", first!.Text);
        Assert.Equal("from-windows", second!.Text);
        var notice = Assert.Single(notices);
        Assert.Equal(OcrEngineRouter.PaddleFallbackNotice, notice);
        Assert.Same(injected, Assert.Single(failures));
        Assert.True(router.IsPaddleDegraded);
        Assert.Equal(2, windows.Calls);
        Assert.Equal(1, paddle.Calls); // 降级后不再重试初始化（14.9.2：不反复重试）
    }

    [Fact]
    public async Task Router_paddle超时异常按失败计_同样永久降级()
    {
        var windows = new FakeEngine(Recognition());
        var paddle = new FakeEngine(throwOnCall: new TimeoutException("单次识别超过 5s"));
        var notices = new List<string>();
        var router = new OcrEngineRouter(
            windows, paddle, () => OcrEngineNames.Paddle, notifyFallback: notices.Add);

        await router.RecognizeAsync([], 1, 1, "auto");

        Assert.True(router.IsPaddleDegraded);
        Assert.Equal(OcrEngineRouter.PaddleFallbackNotice, Assert.Single(notices));
    }

    [Fact]
    public async Task Router_paddle返回null_视为正常无文字结果_不触发降级()
    {
        var windows = new FakeEngine(Recognition());
        var paddle = new FakeEngine(result: null);
        var router = new OcrEngineRouter(windows, paddle, () => OcrEngineNames.Paddle);

        var result = await router.RecognizeAsync([], 1, 1, "auto");

        Assert.Null(result); // null = 输入非法等中性结果，不是故障（故障以异常表达）
        Assert.Equal(1, paddle.Calls);
        Assert.False(router.IsPaddleDegraded);
    }

    // ==================== OcrEngineNames（14.9.3） ====================

    [Theory]
    [InlineData("paddle", "paddle")]
    [InlineData(" Paddle ", "paddle")]
    [InlineData("windows", "windows")]
    [InlineData(null, "windows")]
    [InlineData("", "windows")]
    [InlineData("bogus", "windows")]
    public void Normalize_未知值按默认windows_旧配置向后兼容(string? input, string expected) =>
        Assert.Equal(expected, OcrEngineNames.Normalize(input));

    // ==================== PaddleLayoutMapper：四点框 → 伪词行框（探针固定向量） ====================

    [Fact]
    public void Map_探针S1四点框转外接矩形_单伪词行框与角度()
    {
        var recognition = PaddleLayoutMapper.Map(
            ["The quick brown fox jumps over the lazy dog."],
            [ProbeS1FirstQuad], 980, 328);

        var line = Assert.Single(recognition.Lines);
        Assert.Equal(17, line.Rect.Left);
        Assert.Equal(11, line.Rect.Top);
        Assert.Equal(385, line.Rect.Right);
        Assert.Equal(43, line.Rect.Bottom);
        // 单伪词：词框 = 行框（行框→块→原位替换管线零改动，词框面积门控照常工作）
        var word = Assert.Single(line.Words);
        Assert.Equal(line.Rect, word.Rect);
        Assert.Equal(line.Text, word.Text);
        Assert.Equal(line.Text, recognition.Text);
        // paddle 路径 EngineTag 恒为 null（中英日混训 rec，交回翻译引擎自动检测）
        Assert.Null(recognition.EngineTag);
        // TextAngle：顶边 (17,11)→(385,13)，atan2(2,368) ≈ 0.311°（顺时针为正，与 Windows TextAngle 同约定）
        Assert.NotNull(recognition.TextAngle);
        Assert.Equal(0.311, recognition.TextAngle!.Value, 2);
        Assert.False(OcrLayoutRules.IsTilted(recognition.TextAngle));
    }

    [Fact]
    public void Map_探针S4四点框外接矩形向量()
    {
        var recognition = PaddleLayoutMapper.Map(["line"], [ProbeS4FirstQuad], 760, 208);

        var line = Assert.Single(recognition.Lines);
        Assert.Equal((11, 7, 207, 31), (line.Rect.Left, line.Rect.Top, line.Rect.Right, line.Rect.Bottom));
    }

    [Fact]
    public void Map_倾斜四点框估算角度_与IsTilted既有阈值兼容()
    {
        // 顶边 (20,30)→(120, 30+100·tan5°)：顺时针 5°（屏幕坐标 Y 向下，与 Windows 引擎同约定）
        var dy = 100 * Math.Tan(5 * Math.PI / 180);
        var tilted = PaddleLayoutMapper.Map(
            ["line"], [[(20, 30), (120, 30 + dy), (120, 80 + dy), (20, 80)]], 400, 200);

        Assert.NotNull(tilted.TextAngle);
        Assert.Equal(5.0, tilted.TextAngle!.Value, 1);
        Assert.True(OcrLayoutRules.IsTilted(tilted.TextAngle)); // > 3° → paddle 路径的倾斜降级照常生效
    }

    [Fact]
    public void Map_多行取角度中位数_竖排行不参与()
    {
        var dy2 = 100 * Math.Tan(2 * Math.PI / 180);
        var dy6 = 100 * Math.Tan(6 * Math.PI / 180);
        var recognition = PaddleLayoutMapper.Map(
            ["a", "b", "c"],
            [
                [(20, 30), (120, 30 + dy2), (120, 80 + dy2), (20, 80)],      // 2°
                [(10, 10), (20, 10), (20, 300), (10, 300)],                   // 竖排（宽 < 高）不参与
                [(20, 130), (120, 130 + dy6), (120, 180 + dy6), (20, 180)],   // 6°
            ],
            400, 400);

        Assert.NotNull(recognition.TextAngle);
        Assert.Equal(4.0, recognition.TextAngle!.Value, 1); // 中位数 (2+6)/2
    }

    [Fact]
    public void Map_越界四点框钳制到图像_空文本与空框丢弃_行号重排()
    {
        var recognition = PaddleLayoutMapper.Map(
            ["", "  ", "kept", "degenerate"],
            [
                [(10, 10), (60, 10), (60, 30), (10, 30)],               // 空文本 → 丢弃
                [(10, 10), (60, 10), (60, 30), (10, 30)],               // 纯空白 → 丢弃
                [(-5, -5), (100, -5), (100, 20), (-5, 20)],             // 越界 → 钳制进图像
                [(50, 50), (50, 50), (50, 50), (50, 50)],               // 退化零面积 → 丢弃
            ],
            200, 100);

        var line = Assert.Single(recognition.Lines);
        Assert.Equal(0, line.Index); // 行号按输出顺序重排
        Assert.Equal("kept", line.Text);
        Assert.Equal((0, 0, 100, 20), (line.Rect.Left, line.Rect.Top, line.Rect.Right, line.Rect.Bottom));
        Assert.Equal("kept", recognition.Text);
    }

    [Fact]
    public void Map_多行文本按行拼接_与Windows引擎同语义()
    {
        var recognition = PaddleLayoutMapper.Map(
            ["first line", "second line"],
            [
                [(10, 10), (60, 10), (60, 30), (10, 30)],
                [(10, 40), (60, 40), (60, 60), (10, 60)],
            ],
            200, 100);

        Assert.Equal("first line\nsecond line", recognition.Text);
        Assert.Equal(2, recognition.Lines.Count);
    }

    // ==================== PaddleSessionPolicy：懒加载/空闲释放时序（14.9.2） ====================

    [Fact]
    public void ShouldRelease_空闲满5分钟才释放_常驻永不释放()
    {
        var now = DateTimeOffset.Now;
        var nearExpiry = now - PaddleSessionPolicy.IdleReleaseDelay + TimeSpan.FromSeconds(1);
        var expired = now - PaddleSessionPolicy.IdleReleaseDelay;

        // 未到期 → 保留（重建 ~0.5s 只影响下一次识别，不频繁抖动）
        Assert.False(PaddleSessionPolicy.ShouldRelease(nearExpiry, now, resident: false));
        // 恰好到期 → 释放（宁可早不可拖，保住空闲内存回落 AC）
        Assert.True(PaddleSessionPolicy.ShouldRelease(expired, now, resident: false));
        // 常驻（OcrPaddleResident=true）→ 永不释放
        Assert.False(PaddleSessionPolicy.ShouldRelease(expired, now, resident: true));
        // 从未使用（无会话）→ 无可释放
        Assert.False(PaddleSessionPolicy.ShouldRelease(null, now, resident: false));
    }
}
