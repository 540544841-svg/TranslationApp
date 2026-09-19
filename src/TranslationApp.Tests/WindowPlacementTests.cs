using TranslationApp.Core.Placement;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 小窗物理像素定位（FR-025 / 14.1.3 / 14.1.5）：
/// 「多显示器 + 混合缩放」的全部几何决策都在 <see cref="WindowPlacement"/> 纯函数里，
/// 因此这些用例是本次修复正确性的主要依据（可在单屏机器上穷举负坐标副屏、四角、翻转、超宽、任务栏）。
/// </summary>
public sealed class WindowPlacementTests
{
    // 常用工作区：主屏 1920×1080、任务栏在下方（工作区高 1032）
    private static readonly PhysicalRect PrimaryWork = new(0, 0, 1920, 1032);

    // 副屏在主屏左侧 150%：虚拟桌面物理坐标 Left = -1920
    private static readonly PhysicalRect LeftSecondaryWork = new(-1920, 0, 1920, 1032);

    private static PhysicalRect Window(int width, int height) => new(0, 0, width, height);

    // ---------------- 基准落点：右下偏移 ----------------

    [Fact]
    public void Compute_鼠标在工作区中部时_落在鼠标右下方偏移处()
    {
        var result = WindowPlacement.Compute(400, 300, PrimaryWork, Window(444, 344), 16);

        Assert.Equal(new PhysicalRect(416, 316, 444, 344), result);
    }

    [Fact]
    public void Compute_鼠标在屏幕左上角时_仍向右下偏移()
    {
        var result = WindowPlacement.Compute(PrimaryWork.Left, PrimaryWork.Top, PrimaryWork, Window(444, 344), 16);

        Assert.Equal(new PhysicalRect(16, 16, 444, 344), result);
    }

    // ---------------- 单边翻转 ----------------

    [Fact]
    public void Compute_右边界越界时_翻转到鼠标左侧()
    {
        // 鼠标离右边界仅 100px，右下放置会越界 → 翻到左侧：1900 - 16 - 444 = 1440
        var result = WindowPlacement.Compute(1900, 300, PrimaryWork, Window(444, 344), 16);

        Assert.Equal(1440, result.Left);
        Assert.Equal(316, result.Top);
        Assert.True(result.Right <= PrimaryWork.Right);
    }

    [Fact]
    public void Compute_下边界越界时_翻转到鼠标上方()
    {
        // 工作区高 1032（任务栏在下），鼠标 Y=1000 → 上方：1000 - 16 - 344 = 640
        var result = WindowPlacement.Compute(400, 1000, PrimaryWork, Window(444, 344), 16);

        Assert.Equal(400 + 16, result.Left);
        Assert.Equal(640, result.Top);
        Assert.True(result.Bottom <= PrimaryWork.Bottom);
    }

    [Fact]
    public void Compute_任务栏在上方时_下边界以工作区下沿为准()
    {
        // 工作区 Top = 48（任务栏在上），Bottom = 1080：定位必须用工作区而不是显示器矩形
        var work = new PhysicalRect(0, 48, 1920, 1032);
        var result = WindowPlacement.Compute(400, 600, work, Window(444, 344), 16);

        Assert.Equal(616, result.Top);
        Assert.True(result.Top >= work.Top);
        Assert.True(result.Bottom <= work.Bottom);
    }

    [Fact]
    public void Compute_任务栏在下方时_翻转后不与任务栏重叠()
    {
        var result = WindowPlacement.Compute(400, 1030, PrimaryWork, Window(444, 344), 16);

        Assert.True(result.Bottom <= PrimaryWork.Bottom);
        Assert.True(result.Top >= PrimaryWork.Top);
    }

    // ---------------- 双边同时翻转 ----------------

    [Fact]
    public void Compute_右下同时越界时_同时翻转到左上方()
    {
        // 鼠标贴近右下角：1900 - 16 - 444 = 1440；1000 - 16 - 344 = 640
        var result = WindowPlacement.Compute(1900, 1000, PrimaryWork, Window(444, 344), 16);

        Assert.Equal(new PhysicalRect(1440, 640, 444, 344), result);
    }

    // ---------------- 四角钳制 ----------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1919, 0)]
    [InlineData(0, 1031)]
    [InlineData(1919, 1031)]
    public void Compute_鼠标在四角时_窗口完整落在工作区内(int cursorX, int cursorY)
    {
        var result = WindowPlacement.Compute(cursorX, cursorY, PrimaryWork, Window(444, 344), 16);

        Assert.True(result.Left >= PrimaryWork.Left, $"Left={result.Left}");
        Assert.True(result.Top >= PrimaryWork.Top, $"Top={result.Top}");
        Assert.True(result.Right <= PrimaryWork.Right, $"Right={result.Right}");
        Assert.True(result.Bottom <= PrimaryWork.Bottom, $"Bottom={result.Bottom}");
        Assert.Equal(444, result.Width);
        Assert.Equal(344, result.Height);
    }

    [Fact]
    public void Compute_鼠标在副屏左上角_用负坐标工作区且不越到主屏()
    {
        var result = WindowPlacement.Compute(-1920, 0, LeftSecondaryWork, Window(444, 344), 24);

        Assert.Equal(new PhysicalRect(-1896, 24, 444, 344), result);
        Assert.True(result.Left >= LeftSecondaryWork.Left);
    }

    [Fact]
    public void Compute_鼠标在副屏右下角_翻转后仍留在副屏工作区内()
    {
        var result = WindowPlacement.Compute(-1, 1031, LeftSecondaryWork, Window(444, 344), 24);

        Assert.True(result.Left >= LeftSecondaryWork.Left);
        Assert.True(result.Right <= LeftSecondaryWork.Right);
        Assert.True(result.Bottom <= LeftSecondaryWork.Bottom);
    }

    [Fact]
    public void Compute_鼠标落在两屏之间的缝隙时_以传入的最近屏工作区为基准()
    {
        // MonitorFromPoint(MONITOR_DEFAULTTONEAREST) 已保证取到最近屏；纯函数只认传入的工作区
        var result = WindowPlacement.Compute(-5, 500, LeftSecondaryWork, Window(444, 344), 20);

        Assert.True(result.Left >= LeftSecondaryWork.Left);
        Assert.True(result.Right <= LeftSecondaryWork.Right);
    }

    // ---------------- 窗口比工作区还大 ----------------

    [Fact]
    public void FitToWorkArea_窗口比工作区大时_收缩到工作区尺寸()
    {
        var fitted = WindowPlacement.FitToWorkArea(Window(3000, 2000), new PhysicalRect(0, 0, 1280, 720), 320, 240);

        Assert.Equal(1280, fitted.Width);
        Assert.Equal(720, fitted.Height);
    }

    [Fact]
    public void FitToWorkArea_窗口小于最小尺寸时_抬到最小尺寸()
    {
        var fitted = WindowPlacement.FitToWorkArea(Window(100, 100), PrimaryWork, 320, 240);

        Assert.Equal(320, fitted.Width);
        Assert.Equal(240, fitted.Height);
    }

    [Fact]
    public void FitToWorkArea_最小尺寸本身大于工作区时_以工作区为准保证完整可见()
    {
        var fitted = WindowPlacement.FitToWorkArea(Window(100, 100), new PhysicalRect(0, 0, 200, 150), 320, 240);

        Assert.Equal(200, fitted.Width);
        Assert.Equal(150, fitted.Height);
    }

    [Fact]
    public void FitToWorkArea_尺寸合法时_原样返回且不改动坐标()
    {
        var window = new PhysicalRect(123, 456, 444, 344);
        var fitted = WindowPlacement.FitToWorkArea(window, PrimaryWork, 320, 240);

        Assert.Equal(window, fitted);
    }

    [Fact]
    public void Compute_超宽窗口未先收缩时_也不越出工作区()
    {
        // 防御：即便调用方忘记先 FitToWorkArea，Compute 也不会摆出工作区
        var result = WindowPlacement.Compute(400, 300, PrimaryWork, Window(3000, 2000), 16);

        Assert.True(result.Right <= PrimaryWork.Right);
        Assert.True(result.Bottom <= PrimaryWork.Bottom);
    }

    [Fact]
    public void Compute_窗口等于工作区尺寸时_贴工作区左上角()
    {
        var result = WindowPlacement.Compute(500, 400, PrimaryWork, Window(1920, 1032), 16);

        Assert.Equal(new PhysicalRect(0, 0, 1920, 1032), result);
    }

    [Fact]
    public void Compute_间隙为0时_紧贴鼠标右下()
    {
        var result = WindowPlacement.Compute(400, 300, PrimaryWork, Window(444, 344), 0);

        Assert.Equal(400, result.Left);
        Assert.Equal(300, result.Top);
    }

    // ---------------- 工作区与窗口尺寸不同组合 ----------------

    public static TheoryData<int, int, int, int, int, int> SizeCombinations => new()
    {
        // cursorX, cursorY, windowW, windowH, workW, workH
        { 100, 100, 444, 344, 1920, 1032 },
        { 1900, 100, 600, 400, 1920, 1032 },
        { 100, 1000, 600, 400, 1920, 1032 },
        { 1900, 1000, 600, 400, 1920, 1032 },
        { 500, 400, 480, 360, 800, 600 },
        { 500, 400, 1000, 900, 800, 600 },   // 窗口比工作区大
        { -1900, 50, 444, 344, 1920, 1032 }, // 负坐标副屏
        { -100, 900, 444, 344, 1920, 1032 },
        { 0, 0, 320, 240, 1920, 1032 },      // 最小尺寸窗口
    };

    [Theory]
    [MemberData(nameof(SizeCombinations))]
    public void Compute_任意组合_窗口都完整落在工作区内(
        int cursorX, int cursorY, int windowW, int windowH, int workW, int workH)
    {
        var work = new PhysicalRect(cursorX < 0 ? -workW : 0, 0, workW, workH);
        var window = WindowPlacement.FitToWorkArea(Window(windowW, windowH), work, 320, 240);

        var result = WindowPlacement.Compute(cursorX, cursorY, work, window, 16);

        Assert.True(result.Left >= work.Left, $"Left={result.Left} < {work.Left}");
        Assert.True(result.Top >= work.Top, $"Top={result.Top} < {work.Top}");
        Assert.True(result.Right <= work.Right, $"Right={result.Right} > {work.Right}");
        Assert.True(result.Bottom <= work.Bottom, $"Bottom={result.Bottom} > {work.Bottom}");
    }

    // ---------------- 缩放比下的物理尺寸换算 ----------------

    [Theory]
    [InlineData(1.0, 444)]
    [InlineData(1.25, 555)]
    [InlineData(1.5, 666)]
    [InlineData(2.0, 888)]
    public void ToPhysicalLength_100_125_150_200_缩放下的窗口物理宽度(double scale, int expected)
    {
        // 意图宽度 420 DIP + 两侧 12 DIP 阴影留白 = 444 DIP
        var result = WindowPlacement.ToPhysicalLength(420 + WindowPlacement.ShadowMarginDip * 2, scale);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.0, 344)]
    [InlineData(1.25, 430)]
    [InlineData(1.5, 516)]
    [InlineData(2.0, 688)]
    public void ToPhysicalLength_各缩放下的窗口物理高度(double scale, int expected)
    {
        var result = WindowPlacement.ToPhysicalLength(320 + WindowPlacement.ShadowMarginDip * 2, scale);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.0, 16)]
    [InlineData(1.25, 20)]
    [InlineData(1.5, 24)]
    [InlineData(2.0, 32)]
    public void ToPhysicalLength_间距按DIP语义换算(double scale, int expected)
    {
        // 14.1.2：Gap = 16 DIP（不是恒定的 16 物理像素）
        var result = WindowPlacement.ToPhysicalLength(WindowPlacement.CursorGapDip, scale);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToPhysicalLength_非整数结果四舍五入且结果确定()
    {
        Assert.Equal(19, WindowPlacement.ToPhysicalLength(16, 1.2));   // 19.2
        Assert.Equal(19, WindowPlacement.ToPhysicalLength(16, 1.2));   // 同样输入必须同样输出
        Assert.Equal(17, WindowPlacement.ToPhysicalLength(11.2, 1.5)); // 16.8 → 17
    }

    [Fact]
    public void ToPhysicalLength_缩放非法时按1倍处理()
    {
        Assert.Equal(444, WindowPlacement.ToPhysicalLength(444, 0));
        Assert.Equal(444, WindowPlacement.ToPhysicalLength(444, -1.5));
    }

    [Fact]
    public void Compute_150_缩放的双屏场景_两屏各自定位都在本屏工作区内()
    {
        // 两块 150% 屏相邻：副屏在左（-1920..0），主屏在右（0..1920）；150% 下窗口物理尺寸 666×516，间距 24
        const int gap = 24;
        var window = WindowPlacement.FitToWorkArea(Window(666, 516), PrimaryWork, 480, 360);

        var main = WindowPlacement.Compute(1000, 400, PrimaryWork, window, gap);
        var secondary = WindowPlacement.Compute(-500, 400, LeftSecondaryWork, window, gap);

        Assert.Equal(1024, main.Left);
        Assert.Equal(424, main.Top);
        // 副屏上鼠标距右边缘只有 500px，放不下 666px 宽的窗口 → 翻到鼠标左侧，仍在副屏内
        Assert.Equal(-1190, secondary.Left);
        Assert.Equal(424, secondary.Top);
        Assert.True(main.Right <= PrimaryWork.Right);
        Assert.True(secondary.Right <= LeftSecondaryWork.Right);
    }

    [Fact]
    public void Compute_贴右边缘时_不会跨到相邻屏()
    {
        // 鼠标贴近副屏（负坐标屏）右边缘，翻转后必须仍在副屏内
        var result = WindowPlacement.Compute(-1, 500, LeftSecondaryWork, Window(444, 344), 16);

        Assert.True(result.Right <= LeftSecondaryWork.Right);
        Assert.True(result.Left >= LeftSecondaryWork.Left);
    }

    // ---------------- 稳定性：同一输入多次调用结果一致（对应 AC「同一屏连续多次呼出位置稳定」） ----------------

    [Fact]
    public void Compute_同一输入多次调用_结果完全一致()
    {
        var expected = WindowPlacement.Compute(1234, 567, PrimaryWork, Window(444, 344), 16);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(expected, WindowPlacement.Compute(1234, 567, PrimaryWork, Window(444, 344), 16));
        }
    }

    [Fact]
    public void Compute_右下角呼出连续5次_矩形逐像素一致不漂移()
    {
        var expected = WindowPlacement.Compute(1900, 1000, PrimaryWork, Window(444, 344), 16);
        Assert.Equal(new PhysicalRect(1440, 640, 444, 344), expected);

        for (var i = 0; i < 5; i++)
        {
            var actual = WindowPlacement.Compute(1900, 1000, PrimaryWork, Window(444, 344), 16);
            Assert.Equal(expected.Left, actual.Left);
            Assert.Equal(expected.Top, actual.Top);
            Assert.Equal(expected.Width, actual.Width);
            Assert.Equal(expected.Height, actual.Height);
        }
    }

    [Fact]
    public void Compute_与FitToWorkArea串联后重复执行_尺寸不会被反复收缩()
    {
        var work = new PhysicalRect(0, 0, 800, 600);
        var fitted = WindowPlacement.FitToWorkArea(Window(444, 344), work, 320, 240);

        var first = WindowPlacement.Compute(100, 100, work, fitted, 16);
        var second = WindowPlacement.Compute(100, 100, work, new PhysicalRect(0, 0, first.Width, first.Height), 16);

        Assert.Equal(first, second);
    }
}
