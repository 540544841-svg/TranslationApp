namespace TranslationApp.Core.SystemIntegration;

/// <summary>
/// 选区几何（物理像素）：判断选区的外框是否就在落点附近。纯函数、可单测。
/// <para>
/// 为什么需要它：一个文档里只保留「一段」选区，而这段选区可能是上一步操作留下的。
/// 用户先在网页里选了字、接着去拖滚动条，抬起点也落在同一个文档里——探针照样能问到那段旧选区，
/// 印标就会莫名其妙地跳出来。所以问「有没有选区」之外，还得问「它是不是就在这儿」。
/// </para>
/// </summary>
public static class SelectionGeometry
{
    /// <summary>落点到选区外框的容差：正常拖选的抬起点几乎贴在字上，双击更是落在字里。</summary>
    public const double NearTolerancePx = 32;

    /// <summary>
    /// 一组选区矩形（UIA <c>GetBoundingRectangles</c> 的 [x, y, w, h] × n 格式）里，
    /// 有没有一块挨着落点。传 null 或不足 4 个数表示提供程序没给几何信息——这时**不做判断**（返回 true）：
    /// 「问不到几何」不该变成「看不见印标」。
    /// </summary>
    public static bool IsNear(int x, int y, IReadOnlyList<double>? rects)
    {
        if (rects is null || rects.Count < 4)
        {
            return true;
        }

        for (var i = 0; i + 3 < rects.Count; i += 4)
        {
            var left = rects[i];
            var top = rects[i + 1];
            var dx = Math.Max(Math.Max(left - x, x - (left + rects[i + 2])), 0);
            var dy = Math.Max(Math.Max(top - y, y - (top + rects[i + 3])), 0);
            if (Math.Sqrt((dx * dx) + (dy * dy)) <= NearTolerancePx)
            {
                return true;
            }
        }

        return false;
    }
}
