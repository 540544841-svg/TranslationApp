using TranslationApp.Core.SystemIntegration;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-036：选区几何——印标只在「选区就在落点附近」时才落。</summary>
public class SelectionGeometryTests
{
    // 一块 100×20 的选区，左上角 (200,300)
    private static readonly double[] OneRect = [200, 300, 100, 20];

    [Fact]
    public void PointInsideSelection_IsNear() => Assert.True(SelectionGeometry.IsNear(250, 310, OneRect));

    [Fact]
    public void PointOnSelectionEdge_IsNear() => Assert.True(SelectionGeometry.IsNear(200, 300, OneRect));

    [Fact]
    public void PointJustPastTheLastCharacter_IsNear()
    {
        // 拖选到行尾、手抖多拖了 20px：仍算选中（选区的抬起点几乎总在字边上）
        Assert.True(SelectionGeometry.IsNear(320, 310, OneRect));
    }

    [Fact]
    public void PointFarSideOfTheDocument_IsNotNear()
    {
        // 选了字又去拖滚动条：抬起点离旧选区 400px，不该算到这次头上
        Assert.False(SelectionGeometry.IsNear(600, 310, OneRect));
    }

    [Fact]
    public void PointDiagonallyClose_IsNear() => Assert.True(SelectionGeometry.IsNear(228, 328, OneRect));

    [Fact]
    public void PointDiagonallyFar_IsNotNear() => Assert.False(SelectionGeometry.IsNear(350, 350, OneRect));

    [Fact]
    public void MultilineSelection_AnyLineCounts()
    {
        double[] twoLines = [200, 300, 100, 20, 200, 340, 100, 20];
        Assert.True(SelectionGeometry.IsNear(250, 350, twoLines));
        Assert.False(SelectionGeometry.IsNear(250, 400, twoLines));
    }

    [Fact]
    public void MissingGeometry_DoesNotVeto()
    {
        // 提供程序不给几何信息时不能变成「看不见印标」
        Assert.True(SelectionGeometry.IsNear(9999, 9999, null));
        Assert.True(SelectionGeometry.IsNear(9999, 9999, []));
        Assert.True(SelectionGeometry.IsNear(9999, 9999, [1, 2, 3]));
    }
}
