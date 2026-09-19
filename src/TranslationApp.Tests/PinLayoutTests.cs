using TranslationApp.Core.Capture;
using TranslationApp.Core.Placement;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 钉图的几何与上限判定（FR-027 / 14.3.6 / 14.3.7 / 14.3.10）：
/// 「多显示器 + 混合缩放」下的初始定位、缩放锚点、工作区约束与三重内存上限全部在
/// <see cref="PinLayout"/> 纯函数里，因此这些用例是批 4a 正确性的主要依据
/// （可在单屏机器上穷举负坐标副屏、边界缩放与超限场景）。
/// 贯穿全套的一条不变式：**窗口物理尺寸 = 图像像素 × 缩放倍数**。
/// </summary>
public sealed class PinLayoutTests
{
    // 主屏 1920×1080、任务栏在下方（工作区高 1032）；副屏在主屏左侧 150%（物理坐标 Left = -1920）
    private static readonly PhysicalRect PrimaryWork = new(0, 0, 1920, 1032);

    private static readonly PhysicalRect LeftSecondaryWork = new(-1920, 0, 1920, 1032);

    private static readonly PinZoomLimits DefaultLimits = new(0.25, 4.0);

    // ---------------- 缩放范围解析：上限受工作区约束 ----------------

    [Fact]
    public void ResolveLimits_图像小于工作区时_取设置上下限()
    {
        // 200×100 放到 4 倍仍是 800×400，未触及 1920×1032 的工作区 → 上限就是设置值
        var limits = PinLayout.ResolveLimits(0.25, 4.0, PrimaryWork, 200, 100);

        Assert.Equal(0.25, limits.Min, 10);
        Assert.Equal(4.0, limits.Max, 10);
    }

    [Fact]
    public void ResolveLimits_图像大于工作区时_上限收缩到刚好完整可见()
    {
        // 4000×2000 放不进 1920×1032：0.48（宽）比 0.516（高）更紧 → 上限 = 0.48
        var limits = PinLayout.ResolveLimits(0.25, 4.0, PrimaryWork, 4000, 2000);

        Assert.Equal(0.48, limits.Max, 10);
        Assert.True(limits.Max < 1.0, "比工作区大的图，初始 zoom=1.0 会被夹到 <1，保证完整可见");
    }

    [Fact]
    public void ResolveLimits_图像高度贴着工作区时_上限由高度决定()
    {
        // 800×600：工作区高 1032 → 高度先到顶（1.72），宽度还差得远（2.4）
        var limits = PinLayout.ResolveLimits(0.25, 4.0, PrimaryWork, 800, 600);

        Assert.Equal(1.72, limits.Max, 10);
    }

    [Fact]
    public void ResolveLimits_工作区连下限都放不下时_下限让位且恒可完整显示()
    {
        // 8000×8000 在 800×600 工作区上：fit = 0.075（高度更紧），小于设置下限 0.25 → 下限让位
        var limits = PinLayout.ResolveLimits(0.25, 4.0, new PhysicalRect(0, 0, 800, 600), 8000, 8000);

        Assert.Equal(0.075, limits.Min, 10);
        Assert.Equal(0.075, limits.Max, 10);
        var (width, height) = PinLayout.PhysicalSize(8000, 8000, limits.Max);
        Assert.True(width <= 800 && height <= 600);
    }

    [Fact]
    public void ResolveLimits_设置为非法值或上限小于下限时_回落到安全范围()
    {
        var fallback = PinLayout.ResolveLimits(0, 0, PrimaryWork, 100, 100);
        Assert.Equal(PinLayout.FallbackMin, fallback.Min, 10);
        Assert.Equal(PinLayout.FallbackMax, fallback.Max, 10);

        var inverted = PinLayout.ResolveLimits(2.0, 1.0, PrimaryWork, 100, 100);
        Assert.Equal(2.0, inverted.Min, 10);
        Assert.Equal(2.0, inverted.Max, 10);
    }

    // ---------------- 缩放夹取与步进 ----------------

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.0, 1.0)]
    [InlineData(4.0, 4.0)]
    [InlineData(9.9, 4.0)]
    public void ClampZoom_超出范围时夹取到上下限(double input, double expected)
    {
        Assert.Equal(expected, PinLayout.ClampZoom(input, DefaultLimits), 10);
    }

    [Fact]
    public void ClampZoom_NaN时_按1_0处理()
    {
        Assert.Equal(1.0, PinLayout.ClampZoom(double.NaN, DefaultLimits), 10);
        Assert.Equal(1.0, PinLayout.ClampZoom(0, DefaultLimits), 10); // 非正值同样视为非法 → 1.0
    }

    [Fact]
    public void StepZoom_向上滚动一格_放大为步进倍率()
    {
        Assert.Equal(1.1, PinLayout.StepZoom(1.0, 120, 1.1, fine: false, DefaultLimits), 10);
        Assert.Equal(1.21, PinLayout.StepZoom(1.1, 120, 1.1, fine: false, DefaultLimits), 10);
    }

    [Fact]
    public void StepZoom_向下滚动一格_缩小为步进倍率()
    {
        Assert.Equal(1.0, PinLayout.StepZoom(1.1, -120, 1.1, fine: false, DefaultLimits), 10);
    }

    [Fact]
    public void StepZoom_Shift精细步进_为文档约定的1_02()
    {
        // fine = 1 + (step − 1) × 0.2 = 1 + 0.1 × 0.2 = 1.02（14.3.6）
        Assert.Equal(1.02, PinLayout.StepZoom(1.0, 120, 1.1, fine: true, DefaultLimits), 10);
    }

    [Fact]
    public void StepZoom_到达上限后_保持上限不再变化()
    {
        var atMax = PinLayout.StepZoom(3.9, 120, 1.1, fine: false, DefaultLimits);
        Assert.Equal(4.0, atMax, 10); // 3.9 × 1.1 = 4.29 → 夹到 4.0（调用方据此给边界反馈）

        var again = PinLayout.StepZoom(atMax, 120, 1.1, fine: false, DefaultLimits);
        Assert.Equal(atMax, again, 10); // 值未变 = 已到极限，界面据此提示「已放到最大」
    }

    [Fact]
    public void StepZoom_到达下限后_保持下限不再变化()
    {
        var atMin = PinLayout.StepZoom(0.26, -120, 1.1, fine: false, DefaultLimits);
        Assert.Equal(0.25, atMin, 10);
        Assert.Equal(atMin, PinLayout.StepZoom(atMin, -120, 1.1, fine: false, DefaultLimits), 10);
    }

    [Fact]
    public void StepZoom_一次事件含多格滚动时_按格数幂次累计()
    {
        // 高速滚轮一次可能给 240 = 2 格 → 1.1² = 1.21
        Assert.Equal(1.21, PinLayout.StepZoom(1.0, 240, 1.1, fine: false, DefaultLimits), 10);
    }

    [Fact]
    public void StepZoom_步进非法时_按1_1兜底()
    {
        Assert.Equal(1.1, PinLayout.StepZoom(1.0, 120, 1.0, fine: false, DefaultLimits), 10);
    }

    // ---------------- 物理尺寸 = 图像像素 × 缩放（含 DIP 换算） ----------------

    [Theory]
    [InlineData(800, 600, 1.0, 800, 600)]
    [InlineData(800, 600, 1.5, 1200, 900)]
    [InlineData(800, 600, 2.0, 1600, 1200)]
    [InlineData(801, 601, 0.5, 401, 301)] // 401（0.5 处 away-from-zero）保证结果确定可复现
    public void PhysicalSize_等于图像像素乘缩放(int imageWidth, int imageHeight, double zoom, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), PinLayout.PhysicalSize(imageWidth, imageHeight, zoom));
    }

    [Fact]
    public void PhysicalSize_极端缩放下_不小于1像素()
    {
        Assert.Equal((1, 1), PinLayout.PhysicalSize(10, 10, 0.01));
    }

    /// <summary>AC 9 的基础：物理尺寸 ÷ 所在屏缩放 → DIP，再经 WindowPlacement 还原必须逐像素回到原值。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void DipLength_与WindowPlacement换算互逆(double dpiScale)
    {
        foreach (var imageWidth in new[] { 300, 801, 1200, 2000 })
        {
            var (physicalWidth, _) = PinLayout.PhysicalSize(imageWidth, 100, 1.5);
            var dip = PinLayout.DipLength(physicalWidth, dpiScale);
            Assert.Equal(physicalWidth, WindowPlacement.ToPhysicalLength(dip, dpiScale));
        }
    }

    [Fact]
    public void ContentScale_为窗口DIP宽除以图像像素宽()
    {
        // 1000 图像像素 × zoom 1.5 = 1500 物理像素；在 150% 屏上是 1000 DIP → k = 1.0（与缩放倍数无关）
        Assert.Equal(1.0, PinLayout.ContentScale(1500, 1.5, 1000), 10);
        Assert.Equal(2.0, PinLayout.ContentScale(2000, 1.0, 1000), 10);
    }

    // ---------------- 工作区钳制 ----------------

    [Fact]
    public void ClampIntoWork_尺寸超过工作区时_收缩到工作区大小()
    {
        var result = PinLayout.ClampIntoWork(new PhysicalRect(100, 100, 4000, 3000), PrimaryWork);

        Assert.Equal(1920, result.Width);
        Assert.Equal(1032, result.Height);
        Assert.True(result.Right <= PrimaryWork.Right && result.Bottom <= PrimaryWork.Bottom);
    }

    [Fact]
    public void ClampIntoWork_位置越界时_平移到工作区内()
    {
        var right = PinLayout.ClampIntoWork(new PhysicalRect(1900, 1000, 400, 300), PrimaryWork);
        Assert.Equal(1520, right.Left);
        Assert.Equal(732, right.Top);

        var negative = PinLayout.ClampIntoWork(new PhysicalRect(-500, -400, 400, 300), PrimaryWork);
        Assert.Equal(0, negative.Left);
        Assert.Equal(0, negative.Top);
    }

    [Fact]
    public void ClampIntoWork_副屏负坐标_钳制到副屏工作区而非主屏()
    {
        // 副屏工作区 [-1920, 0]：矩形右边缘 300 越过了副屏右边界 → 左移到 -400（右边缘贴合 0）
        var result = PinLayout.ClampIntoWork(new PhysicalRect(-100, 100, 400, 300), LeftSecondaryWork);

        Assert.Equal(-400, result.Left);
        Assert.Equal(0, result.Right);
        Assert.True(result.Left >= LeftSecondaryWork.Left);
    }

    // ---------------- 初始定位：钉在原选区位置 ----------------

    [Fact]
    public void InitialRect_zoom为1_时_与原选区逐像素一致()
    {
        // 选区在显示器内 (300,200)、尺寸 500×400；显示器在主屏原点
        var rect = PinLayout.InitialRect(300, 200, 500, 400, PrimaryWork, 1.0);

        Assert.Equal(new PhysicalRect(300, 200, 500, 400), rect);
    }

    [Fact]
    public void InitialRect_副屏负坐标时_位置正确()
    {
        // 副屏在左侧：显示器物理 Left = -1920，选区图像 X = 600 → 屏幕物理 X = -1320
        var rect = PinLayout.InitialRect(-1920 + 600, 300, 400, 300, LeftSecondaryWork, 1.0);

        Assert.Equal(new PhysicalRect(-1320, 300, 400, 300), rect);
    }

    [Fact]
    public void InitialRect_选区右下角溢出工作区时_平移回工作区内()
    {
        // 选区贴屏幕右下角：480 + 500 = 980 > 1920? 否；用极小工作区验证平移
        var work = new PhysicalRect(0, 0, 1000, 800);
        var rect = PinLayout.InitialRect(600, 500, 500, 400, work, 1.0);

        Assert.Equal(500, rect.Left);
        Assert.Equal(400, rect.Top);
        Assert.True(rect.Right <= work.Right && rect.Bottom <= work.Bottom);
    }

    [Fact]
    public void InitialRect_放不下时_收缩到工作区且完整可见()
    {
        // 选区 2400×1400 比工作区 1920×1032 还大 → 收缩到工作区大小并贴到工作区左上角
        var rect = PinLayout.InitialRect(100, 100, 2400, 1400, PrimaryWork, 1.0);

        Assert.Equal(PrimaryWork.Width, rect.Width);
        Assert.Equal(PrimaryWork.Height, rect.Height);
        Assert.True(rect.Left >= PrimaryWork.Left && rect.Top >= PrimaryWork.Top);
        Assert.True(rect.Right <= PrimaryWork.Right && rect.Bottom <= PrimaryWork.Bottom);
    }

    [Fact]
    public void InitialRect_缩放后尺寸随倍数变化()
    {
        var rect = PinLayout.InitialRect(100, 100, 400, 300, PrimaryWork, 1.5);

        Assert.Equal(600, rect.Width);
        Assert.Equal(450, rect.Height);
        Assert.Equal(100, rect.Left);
        Assert.Equal(100, rect.Top);
    }

    // ---------------- 缩放锚点 ----------------

    [Fact]
    public void ZoomAt_锚点分数对应的屏幕点保持不动()
    {
        var current = new PhysicalRect(600, 400, 400, 300);
        const double fracX = 0.25;
        const double fracY = 0.5;
        var anchorX = current.Left + fracX * current.Width;  // 700
        var anchorY = current.Top + fracY * current.Height;  // 550

        var zoomed = PinLayout.ZoomAt(current, fracX, fracY, 2.0, 400, 300, PrimaryWork);

        Assert.Equal(800, zoomed.Width);
        Assert.Equal(600, zoomed.Height);
        Assert.Equal((int)Math.Round(anchorX - fracX * zoomed.Width), zoomed.Left);
        Assert.Equal((int)Math.Round(anchorY - fracY * zoomed.Height), zoomed.Top);
    }

    [Fact]
    public void ZoomAt_鼠标在窗口中心时_四边等距扩张()
    {
        var current = new PhysicalRect(400, 300, 400, 300);

        var zoomed = PinLayout.ZoomAt(current, 0.5, 0.5, 2.0, 400, 300, PrimaryWork);

        Assert.Equal(new PhysicalRect(200, 150, 800, 600), zoomed);
    }

    [Fact]
    public void ZoomAt_鼠标在左上角时_左上角不动()
    {
        var current = new PhysicalRect(400, 300, 400, 300);

        var zoomed = PinLayout.ZoomAt(current, 0, 0, 2.0, 400, 300, PrimaryWork);

        Assert.Equal(400, zoomed.Left);
        Assert.Equal(300, zoomed.Top);
    }

    [Fact]
    public void ZoomAt_放大后越过工作区边界时_仍完整可见()
    {
        // 窗口贴右下角放大：锚点数学算出的位置必然越界 → 必须被钳制回工作区
        var current = new PhysicalRect(1400, 700, 400, 300);

        var zoomed = PinLayout.ZoomAt(current, 0.0, 0.0, 4.0, 400, 300, PrimaryWork);

        Assert.True(zoomed.Right <= PrimaryWork.Right);
        Assert.True(zoomed.Bottom <= PrimaryWork.Bottom);
        Assert.True(zoomed.Left >= PrimaryWork.Left);
        Assert.True(zoomed.Top >= PrimaryWork.Top);
    }

    [Fact]
    public void ZoomAt_锚点分数非法时_退化为中心锚点()
    {
        var current = new PhysicalRect(400, 300, 400, 300);

        var zoomed = PinLayout.ZoomAt(current, double.NaN, double.NaN, 2.0, 400, 300, PrimaryWork);

        Assert.Equal(new PhysicalRect(200, 150, 800, 600), zoomed);
    }

    // ---------------- 单张像素上限：降采样 ----------------

    [Fact]
    public void FitToPixelLimit_未超限时_不缩放()
    {
        var (width, height, ratio, downscaled) = PinLayout.FitToPixelLimit(1200, 800, 4_000_000);

        Assert.Equal(1200, width);
        Assert.Equal(800, height);
        Assert.Equal(1.0, ratio, 10);
        Assert.False(downscaled);
    }

    [Fact]
    public void FitToPixelLimit_超限时_等比缩小到上限以内()
    {
        // 4000×3000 = 12 MP → 4 MP 上限，面积缩到 1/3
        var (width, height, ratio, downscaled) = PinLayout.FitToPixelLimit(4000, 3000, 4_000_000);

        Assert.True(downscaled);
        Assert.True((long)width * height <= 4_000_000, $"降采样后 {width}×{height} 仍超上限");
        Assert.Equal(4000.0 / 3000.0, width / (double)height, 2); // 长宽比保持
        Assert.InRange(ratio, 0.5, 0.6);
    }

    [Fact]
    public void FitToPixelLimit_取整后仍略超上限时_继续收缩()
    {
        var (width, height, _, downscaled) = PinLayout.FitToPixelLimit(3001, 3001, 4_000_000);

        Assert.True(downscaled);
        Assert.True((long)width * height <= 4_000_000);
    }

    [Fact]
    public void FitToPixelLimit_非法输入时_原样返回()
    {
        Assert.Equal((0, 100, 1.0, false), PinLayout.FitToPixelLimit(0, 100, 4_000_000));
        Assert.Equal((100, 100, 1.0, false), PinLayout.FitToPixelLimit(100, 100, 0));
    }

    // ---------------- 张数与像素总量上限 ----------------

    [Fact]
    public void CheckBudget_未超限时_放行()
    {
        Assert.Equal(PinBudgetKind.Ok, PinLayout.CheckBudget(2, 2_000_000, 1_000_000, 5, 12_000_000));
    }

    [Fact]
    public void CheckBudget_张数达到上限时_拒绝()
    {
        Assert.Equal(PinBudgetKind.CountExceeded, PinLayout.CheckBudget(5, 1_000_000, 10_000, 5, 12_000_000));
    }

    [Fact]
    public void CheckBudget_像素总量将超上限时_拒绝()
    {
        // 11 MP + 2 MP > 12 MP（虽然张数与单张都不超）
        Assert.Equal(PinBudgetKind.TotalExceeded, PinLayout.CheckBudget(4, 11_000_000, 2_000_000, 5, 12_000_000));
    }

    [Fact]
    public void CheckBudget_恰好等于总量上限时_放行()
    {
        Assert.Equal(PinBudgetKind.Ok, PinLayout.CheckBudget(1, 8_000_000, 4_000_000, 5, 12_000_000));
    }

    [Fact]
    public void CheckBudget_张数与总量同时超限时_优先报张数()
    {
        Assert.Equal(PinBudgetKind.CountExceeded, PinLayout.CheckBudget(6, 12_000_000, 4_000_000, 5, 12_000_000));
    }

    [Fact]
    public void CheckBudget_上限为0或负时_视为不限制()
    {
        Assert.Equal(PinBudgetKind.Ok, PinLayout.CheckBudget(99, 999_999_999, 999_999_999, 0, 0));
    }

    // ---------------- 链路分流（OcrOutputMode） ----------------

    [Theory]
    [InlineData("pin", "pin")]
    [InlineData("text", "text")]
    [InlineData("both", "both")]
    [InlineData("TEXT", "text")]
    [InlineData(" both ", "both")]
    [InlineData("", "pin")]
    [InlineData(null, "pin")]
    [InlineData("unknown", "pin")]
    public void OcrOutputMode_Parse_未知值一律回落默认pin(string? input, string expected)
    {
        Assert.Equal(expected, OcrOutputMode.Parse(input));
    }

    [Theory]
    [InlineData("pin", true, false)]
    [InlineData("both", true, true)]
    [InlineData("text", false, true)]
    [InlineData("bad-value", true, false)]
    public void OcrOutputMode_分流判定(string mode, bool pinsImage, bool usesQuickWindow)
    {
        Assert.Equal(pinsImage, OcrOutputMode.PinsImage(mode));
        Assert.Equal(usesQuickWindow, OcrOutputMode.UsesQuickWindow(mode));
    }
}
