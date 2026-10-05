using TranslationApp.Core.Layout;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 小窗尺寸策略（FR-026 / 14.2，阶段 5 批 3 改版）：
/// 「默认宽高 → 按内容自适应后的最终宽高」的纯函数计算——只增不减、宽度 640 上限
/// （默认值可越过）、范围夹取、自适应关闭不做测量、同一次呼出内重复计算稳定。
/// </summary>
public sealed class WindowSizePolicyTests
{
    /// <summary>1080p 屏工作区高（任务栏在下方）。</summary>
    private const double WorkArea1080 = 1032;

    /// <summary>1366×768 屏（14.2.3 的示例屏）。</summary>
    private const double WorkArea768 = 768;

    private const double DefaultWidth = WindowSizePolicy.DefaultWidthDip;   // 设计稿 472 + 阴影留白 24
    private const double DefaultHeight = WindowSizePolicy.DefaultHeightDip; // 设计稿 266 + 阴影留白 24

    // ---------------- 宽度：只增不减 + 640 上限（14.2） ----------------

    [Fact]
    public void ResolveWidth_短内容保持默认宽度不缩水()
    {
        // 只增不减：内容只需 120 DIP 时仍用默认宽度
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, 120, adaptToContent: true));
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, 0, adaptToContent: true));
    }

    [Fact]
    public void ResolveWidth_长内容按需加宽且封顶640()
    {
        Assert.Equal(560, WindowSizePolicy.ResolveWidth(DefaultWidth, 560, adaptToContent: true));
        // 超过 640：自适应加宽到 640 就停
        Assert.Equal(640, WindowSizePolicy.ResolveWidth(DefaultWidth, 1000, adaptToContent: true));
        Assert.Equal(640, WindowSizePolicy.ResolveWidth(DefaultWidth, 9000, adaptToContent: true));
        Assert.Equal(640, WindowSizePolicy.MaxContentWidthDip(DefaultWidth));
    }

    [Fact]
    public void ResolveWidth_默认宽度超过640时以默认值为准且不再加宽()
    {
        // 640 只约束「自适应加宽」这个动作，不约束用户设的默认值
        Assert.Equal(800, WindowSizePolicy.ResolveWidth(800, 100, adaptToContent: true));
        Assert.Equal(800, WindowSizePolicy.ResolveWidth(800, 5000, adaptToContent: true));
        Assert.Equal(800, WindowSizePolicy.MaxContentWidthDip(800));

        // 恰好等于 640：加宽能力为零，取值仍是 640
        Assert.Equal(640, WindowSizePolicy.ResolveWidth(640, 5000, adaptToContent: true));
    }

    [Fact]
    public void ResolveWidth_测量失败时保持默认宽度()
    {
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, double.NaN, adaptToContent: true));
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, -5, adaptToContent: true));
    }

    [Fact]
    public void ResolveWidth_自适应关闭时等于默认宽度()
    {
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, 5000, adaptToContent: false));
        Assert.Equal(700, WindowSizePolicy.ResolveWidth(700, 100, adaptToContent: false));
    }

    // ---------------- 宽度需求按「目标行数」折算（避免稍长文本直接顶到上限） ----------------

    [Theory]
    [InlineData(0)]                 // 无内容
    [InlineData(-10)]               // 异常值
    [InlineData(double.NaN)]        // 测量失败
    public void RequiredWidthForLineTarget_无有效宽度时返回0(double unwrapped)
    {
        Assert.Equal(0, WindowSizePolicy.RequiredWidthForLineTarget(unwrapped));
    }

    [Fact]
    public void RequiredWidthForLineTarget_按目标行数折算()
    {
        const int lines = WindowSizePolicy.AdaptiveWidthTargetLines;

        Assert.Equal(3000.0 / lines, WindowSizePolicy.RequiredWidthForLineTarget(3000), 3);
        Assert.Equal(3000.0 / 3, WindowSizePolicy.RequiredWidthForLineTarget(3000, 3), 3);
    }

    /// <summary>
    /// A-⑤ 回归：宽度自适应目标行数 5 → 3。5 行目标下译文少于约 145 个中文字符永不加宽、
    /// 首次加宽仅 +10 DIP 不可感知；3 行下同一不限宽宽度的折算值（1452 → 484）必须稳定。
    /// </summary>
    [Fact]
    public void RequiredWidthForLineTarget_3行目标的回归值()
    {
        Assert.Equal(3, WindowSizePolicy.AdaptiveWidthTargetLines);
        Assert.Equal(484, WindowSizePolicy.RequiredWidthForLineTarget(1452), 3);
        Assert.Equal(484, WindowSizePolicy.RequiredWidthForLineTarget(1452, 3), 3);
    }

    /// <summary>
    /// A-⑤ 组合用例（按 14.5 DIP/字的中文典型测量）：50 字保持默认 420（只增不减）、
    /// 100 字加宽到约 484（首次加宽即可感知，+64 DIP）、150 字封顶 640。
    /// </summary>
    [Fact]
    public void 折算宽度_3行下50字保持默认100字感知加宽150字封顶()
    {
        const double defaultWidth = 420;

        // 50 字 ≈ 727 DIP 不限宽 → /3 ≈ 242 < 420 → 保持默认宽度
        Assert.Equal(420, WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(727), adaptToContent: true));

        // 100 字 ≈ 1452 DIP 不限宽 → /3 = 484 → 可感知加宽且不到上限
        var medium = WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(1452), adaptToContent: true);
        Assert.Equal(484, medium, 3);
        Assert.InRange(medium, 460, 510);

        // 150 字 ≈ 2100 DIP 不限宽 → /3 = 700 > 640 → 封顶
        Assert.Equal(640, WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(2100), adaptToContent: true));
    }

    /// <summary>
    /// 这是本规则存在的理由：不限宽宽度直接当需求会让"中文约 50 字"就撞到 640 上限，
    /// 观感上变成 420 或 640 两档跳；折算后短/中/长三档应呈现渐进增长。
    /// </summary>
    [Fact]
    public void 折算后宽度随内容量渐进增长而非常量顶格()
    {
        const double defaultWidth = 420;

        // 30 字中文（约 440 不限宽）→ 折算后低于默认值 → 保持默认宽度
        var shortText = WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(440), adaptToContent: true);
        Assert.Equal(defaultWidth, shortText);

        // 中等长度（约 1500 不限宽）→ 折算到 (420, 640) 之间，即"小幅加宽"
        var mediumText = WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(1500), adaptToContent: true);
        Assert.True(mediumText > defaultWidth && mediumText < 640,
            $"中等长度文本应加宽但不顶格，实际 {mediumText}");

        // 很长（约 5600 不限宽）→ 折算后超过上限 → 封顶 640
        var longText = WindowSizePolicy.ResolveWidth(
            defaultWidth, WindowSizePolicy.RequiredWidthForLineTarget(5600), adaptToContent: true);
        Assert.Equal(640, longText);
    }

    // ---------------- 高度：默认值基准 + 只增不减 + 内容上限（14.2.3） ----------------

    [Fact]
    public void ResolveHeight_短内容等于默认高度()
    {
        // 空输入/短译文：窗口高度就是默认高度（不再是最小高度 240 起算）
        Assert.Equal(DefaultHeight, WindowSizePolicy.ResolveHeight(DefaultHeight, 120, DefaultHeight, true, WorkArea1080));
        Assert.Equal(DefaultHeight, WindowSizePolicy.ResolveHeight(DefaultHeight, 200, DefaultHeight, true, WorkArea1080));
    }

    [Fact]
    public void ResolveHeight_长内容按内容增长()
    {
        Assert.Equal(500, WindowSizePolicy.ResolveHeight(DefaultHeight, 500, DefaultHeight, true, WorkArea1080));
    }

    [Fact]
    public void ResolveHeight_内容超上限时封顶并保持()
    {
        // 1080p：0.80 × 1032 ≈ 826 → 与 640 取小 = 640
        Assert.Equal(640, WindowSizePolicy.ResolveHeight(DefaultHeight, 3000, DefaultHeight, true, WorkArea1080));
        Assert.Equal(640, WindowSizePolicy.MaxContentHeightDip(WorkArea1080));

        // 1366×768：0.80 × 768 = 614.4 → 取比例值
        Assert.Equal(614.4, WindowSizePolicy.MaxContentHeightDip(WorkArea768), 3);
        Assert.Equal(614.4, WindowSizePolicy.ResolveHeight(DefaultHeight, 3000, DefaultHeight, true, WorkArea768), 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxContentHeightDip_工作区不可知时只用绝对上限(double unknownWorkArea)
    {
        // 取显示器信息失败时的降级路径：不能因为拿不到工作区就放弃上限
        Assert.Equal(WindowSizePolicy.MaxHeightCapDip, WindowSizePolicy.MaxContentHeightDip(unknownWorkArea));
    }

    [Fact]
    public void ClampContentHeight_工作区比最小高度还矮时以工作区为准()
    {
        // 极小工作区（虚拟屏/异常值）：min 不能大于 max，否则 Math.Clamp 会抛异常
        Assert.Equal(160, WindowSizePolicy.ClampContentHeight(1000, 200));
    }

    [Fact]
    public void ResolveHeight_会话内只增不减()
    {
        // 同一会话内一旦为容纳内容放大过，内容变短也不回缩
        Assert.Equal(400, WindowSizePolicy.ResolveHeight(DefaultHeight, 300, 400, true, WorkArea1080));
        Assert.Equal(500, WindowSizePolicy.ResolveHeight(DefaultHeight, 300, 500, true, WorkArea1080));
    }

    [Fact]
    public void ResolveHeight_默认高度超过内容上限时以默认值为准()
    {
        // 与宽度同一原则：默认值（用户偏好）可越过内容上限 640
        Assert.Equal(800, WindowSizePolicy.ResolveHeight(800, 300, 800, true, WorkArea1080));
        Assert.Equal(800, WindowSizePolicy.ResolveHeight(800, 3000, 800, true, WorkArea1080));
    }

    [Theory]
    [InlineData(double.NaN)] // 测量失败（DesiredSize 为 NaN）
    [InlineData(0)]          // 测量失败（DesiredSize 为 0）
    [InlineData(-5)]
    public void ResolveHeight_测量失败时保持当前尺寸(double brokenMeasurement)
    {
        // AC：极端文本也不崩、尺寸保持合理（不缩小、不为 0）
        Assert.Equal(DefaultHeight, WindowSizePolicy.ResolveHeight(DefaultHeight, brokenMeasurement, DefaultHeight, true, WorkArea1080));
        Assert.Equal(400, WindowSizePolicy.ResolveHeight(DefaultHeight, brokenMeasurement, 400, true, WorkArea1080));
    }

    [Fact]
    public void ResolveHeight_自适应关闭时等于默认高度()
    {
        Assert.Equal(DefaultHeight, WindowSizePolicy.ResolveHeight(DefaultHeight, 5000, 320, false, WorkArea1080));
        Assert.Equal(500, WindowSizePolicy.ResolveHeight(500, 100, 500, false, WorkArea1080));
    }

    // ---------------- 同一次呼出的稳定性（防抖动） ----------------

    [Fact]
    public void ResolveSize_同一会话内重复计算结果稳定()
    {
        // 内容不变 → 反复计算（呼出、翻译返回、防抖重算）结果必须完全一致，否则窗口会来回抽动
        var first = WindowSizePolicy.ResolveHeight(DefaultHeight, 512.5, DefaultHeight, true, WorkArea1080);
        var second = WindowSizePolicy.ResolveHeight(DefaultHeight, 512.5, first, true, WorkArea1080);
        var third = WindowSizePolicy.ResolveHeight(DefaultHeight, 512.5, second, true, WorkArea1080);

        Assert.Equal(first, second);
        Assert.Equal(second, third);
        Assert.Equal(512.5, first, 3);

        // 宽度同理：同一测量值反复计算无漂移
        Assert.Equal(
            WindowSizePolicy.ResolveWidth(DefaultWidth, 610, true),
            WindowSizePolicy.ResolveWidth(DefaultWidth, 610, true));
    }

    [Fact]
    public void ResolveSize_每次呼出回到默认值()
    {
        // 上一会话被拖到/长到 900：会话基线重置为默认高度后，短内容立即回到 320
        Assert.Equal(
            DefaultHeight,
            WindowSizePolicy.ResolveHeight(DefaultHeight, 120, WindowSizePolicy.DefaultHeightDip, true, WorkArea1080));
        Assert.Equal(DefaultWidth, WindowSizePolicy.ResolveWidth(DefaultWidth, 0, true));
    }

    // ---------------- 范围夹取（设置项 14.6） ----------------

    [Theory]
    [InlineData(100, 320)]
    [InlineData(319.9, 320)]
    [InlineData(320, 320)]
    [InlineData(640, 640)]
    [InlineData(900, 900)]
    [InlineData(1200, 900)]
    public void ClampWidth_夹取到320至900(double input, double expected) =>
        Assert.Equal(expected, WindowSizePolicy.ClampWidth(input));

    [Theory]
    [InlineData(100, 240)]
    [InlineData(239.9, 240)]
    [InlineData(240, 240)]
    [InlineData(640, 640)]
    [InlineData(900, 900)]
    [InlineData(2000, 900)]
    public void ClampHeight_夹取到240至900(double input, double expected) =>
        Assert.Equal(expected, WindowSizePolicy.ClampHeight(input));

    [Fact]
    public void ClampWidthHeight_配置损坏为NaN时回退推荐默认值()
    {
        Assert.Equal(WindowSizePolicy.DefaultWidthDip, WindowSizePolicy.ClampWidth(double.NaN));
        Assert.Equal(WindowSizePolicy.DefaultHeightDip, WindowSizePolicy.ClampHeight(double.NaN));
    }

    [Fact]
    public void NormalizeAsDefault_设为默认尺寸时按范围夹取并保留一位小数()
    {
        // 「设为默认尺寸」：拖到超大/超小也要落回可持久化的范围
        Assert.Equal((420, 320), WindowSizePolicy.NormalizeAsDefault(420, 320));
        Assert.Equal((900, 900), WindowSizePolicy.NormalizeAsDefault(1500, 1500));
        Assert.Equal((320, 240), WindowSizePolicy.NormalizeAsDefault(10, 10));
        Assert.Equal((512.3, 400.7), WindowSizePolicy.NormalizeAsDefault(512.34, 400.68));
    }

    // ---------------- 模式判定与默认常量（14.6） ----------------

    [Theory]
    [InlineData("manual", true)]
    [InlineData("Manual", true)]
    [InlineData("MANUAL", true)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    public void IsManual_仅manual为固定尺寸模式(string? mode, bool expected) =>
        Assert.Equal(expected, WindowSizePolicy.IsManual(mode));

    [Theory]
    [InlineData("manual", "manual")]
    [InlineData("Manual", "manual")]
    [InlineData("auto", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("garbage", "auto")]
    public void NormalizeMode_未知值一律视为默认的自适应模式(string? mode, string expected) =>
        Assert.Equal(expected, WindowSizePolicy.NormalizeMode(mode));

    [Fact]
    public void 默认参数与文档一致()
    {
        // 14.2 / 14.6：默认 496×290（设计稿卡片 472×266 + 四周 12 阴影留白）、范围 320~900 与 240~900、
        // 自适应加宽上限 640、内容高度上限 640
        Assert.Equal("auto", WindowSizePolicy.AutoMode);
        Assert.Equal("manual", WindowSizePolicy.ManualMode);
        Assert.Equal(496, WindowSizePolicy.DefaultWidthDip);
        Assert.Equal(290, WindowSizePolicy.DefaultHeightDip);
        Assert.Equal(320, WindowSizePolicy.MinWidthDip);
        Assert.Equal(900, WindowSizePolicy.MaxWidthDip);
        Assert.Equal(240, WindowSizePolicy.MinHeightDip);
        Assert.Equal(900, WindowSizePolicy.MaxHeightDip);
        Assert.Equal(640, WindowSizePolicy.AdaptiveWidthCapDip);
        Assert.Equal(640, WindowSizePolicy.MaxHeightCapDip);
    }

    [Fact]
    public void 默认宽高始终落在允许范围内()
    {
        // 防御：推荐默认值若被改坏（超出范围），「恢复推荐默认值」会把设置写成非法值
        Assert.InRange(WindowSizePolicy.DefaultWidthDip, WindowSizePolicy.MinWidthDip, WindowSizePolicy.MaxWidthDip);
        Assert.InRange(WindowSizePolicy.DefaultHeightDip, WindowSizePolicy.MinHeightDip, WindowSizePolicy.MaxHeightDip);
    }

    // ---------------- 防抖合并（14.2.4 时机 3/5：更早的到期时间赢） ----------------

    [Fact]
    public void MergeDebounceMs_没有挂起时采用本次请求的时长()
    {
        Assert.Equal(400, WindowSizePolicy.MergeDebounceMs(0, 400));
        Assert.Equal(120, WindowSizePolicy.MergeDebounceMs(0, 120));
    }

    [Fact]
    public void MergeDebounceMs_已挂起短防抖时长防抖不得把它顶掉()
    {
        // 这是本函数存在的理由：译文返回的 120ms 必须活过紧随其后的 StatusText / IsBusy(400ms)
        Assert.Equal(120, WindowSizePolicy.MergeDebounceMs(120, 400));
    }

    [Fact]
    public void MergeDebounceMs_已挂起长防抖时短防抖可提前()
    {
        // 反向不阻塞：先 IsBusy(400ms)、后 ResultText(120ms) 时按 120ms 算（越早越准）
        Assert.Equal(120, WindowSizePolicy.MergeDebounceMs(400, 120));
        Assert.Equal(200, WindowSizePolicy.MergeDebounceMs(400, 200));
        Assert.Equal(400, WindowSizePolicy.MergeDebounceMs(400, 400));
    }

    [Fact]
    public void MergeDebounceMs_翻译成功收尾整批属性变更合并为短防抖()
    {
        // 真实顺序：ResultText(120ms) → StatusText(400ms) → IsBusy=false(400ms)
        var pending = WindowSizePolicy.MergeDebounceMs(0, 120);
        pending = WindowSizePolicy.MergeDebounceMs(pending, 400);
        pending = WindowSizePolicy.MergeDebounceMs(pending, 400);
        Assert.Equal(120, pending);

        // 开始翻译的顺序：IsBusy=true(400ms) → ErrorText(400ms) → ResultText 清空(120ms) → StatusText(400ms)
        pending = WindowSizePolicy.MergeDebounceMs(0, 400);
        pending = WindowSizePolicy.MergeDebounceMs(pending, 400);
        pending = WindowSizePolicy.MergeDebounceMs(pending, 120);
        pending = WindowSizePolicy.MergeDebounceMs(pending, 400);
        Assert.Equal(120, pending);
    }
}
