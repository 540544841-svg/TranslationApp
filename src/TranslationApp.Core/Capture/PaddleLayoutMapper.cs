namespace TranslationApp.Core.Capture;

/// <summary>
/// PaddleOCR（RapidOcrNet）检测框 → 统一布局的纯映射（FR-030 / 14.9.1「词/行框输出」）：
/// det 四点框 → 外接矩形 → **单伪词** <see cref="OcrLineBox"/>（与 FR-029-2 云端行框方案同构），
/// 行框→块→原位替换管线（<see cref="OcrBlockGrouping"/> / OverlayLayout）零改动；
/// 行框即唯一词框，词框总面积 = Σ 行框面积，与 <see cref="OcrScriptScoring"/> 面积门控、
/// <see cref="OcrPreprocess"/> 的 scale² 归一（预处理增强候选还原）同口径兼容。
/// 纯函数放 Core（无 RapidOcrNet/SkiaSharp 依赖）：App 层适配器把 <c>SKPointI[]</c> 转成坐标对后调用，
/// 单测直接用 C0 探针（D:\ocrprobe\report.md）的固定样本输出做向量。
/// </summary>
public static class PaddleLayoutMapper
{
    /// <summary>
    /// det 四点框（顺序无关）→ 外接矩形，钳制到图像范围内（Windows 词框不会越界，paddle 对齐同口径）。
    /// 钳制后宽/高 ≤ 0（空矩形）的检测框视为无效。
    /// </summary>
    public static OcrRect BoundingRect(IReadOnlyList<(double X, double Y)> boxPoints, int imageWidth, int imageHeight)
    {
        if (boxPoints.Count == 0)
        {
            return default;
        }

        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (x, y) in boxPoints)
        {
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        return new OcrRect(left, top, right, bottom).ClampTo(imageWidth, imageHeight);
    }

    /// <summary>
    /// TextAngle 估算（14.9.2）：取每个「横向行」（外接框宽 &gt; 高）四点框顶边两端点的倾角——
    /// 屏幕坐标 Y 向下，<c>atan2(dy, dx)</c> 以顺时针为正，与 Windows.Media.Ocr 的 TextAngle 同约定；
    /// 多行取中位数（倾斜文本各行角度一致，中位数对个别框抖动稳健）。无横向行返回 null。
    /// 角度阈值仍由 <see cref="OcrLayoutRules.IsTilted"/>（&gt; 3°）判定：windows 引擎的 −0/null 在
    /// paddle 路径变成真实角度，倾斜降级（SidePanel）判定因此照常甚至更早生效。
    /// </summary>
    public static double? EstimateTextAngle(IReadOnlyList<(double X, double Y)[]> boxPoints, int imageWidth, int imageHeight)
    {
        var angles = new List<double>();
        foreach (var quad in boxPoints)
        {
            var rect = BoundingRect(quad, imageWidth, imageHeight);
            if (rect.IsEmpty || rect.Width <= rect.Height)
            {
                continue; // 竖排/近方块的顶边无横向语义，不参与
            }

            if (TryTopEdge(quad, out var tl, out var tr))
            {
                angles.Add(Math.Atan2(tr.Y - tl.Y, tr.X - tl.X) * 180.0 / Math.PI);
            }
        }

        if (angles.Count == 0)
        {
            return null;
        }

        angles.Sort();
        var mid = angles.Count / 2;
        return angles.Count % 2 == 1 ? angles[mid] : (angles[mid - 1] + angles[mid]) / 2.0;
    }

    /// <summary>顶边两端点：按 Y 升序取前两点（Y 相同取 X 小者在前），再按 X 排成 (左上, 右上)。</summary>
    private static bool TryTopEdge(
        IReadOnlyList<(double X, double Y)> quad, out (double X, double Y) topLeft, out (double X, double Y) topRight)
    {
        topLeft = topRight = default;
        if (quad.Count < 2)
        {
            return false;
        }

        var ordered = quad.OrderBy(p => p.Y).ThenBy(p => p.X).Take(2).OrderBy(p => p.X).ToArray();
        (topLeft, topRight) = (ordered[0], ordered[1]);
        return true;
    }

    /// <summary>
    /// RapidOcrNet 检测结果 → <see cref="OcrRecognition"/>（C2 适配器的唯一映射出口）：
    /// 每个四点框一行、行内一个伪词（词框 = 行框）；空文本或空框的检测块被丢弃，行号按输出顺序重排。
    /// </summary>
    public static OcrRecognition Map(
        IReadOnlyList<string> texts,
        IReadOnlyList<(double X, double Y)[]> boxPoints,
        int imageWidth,
        int imageHeight)
    {
        var lines = new List<OcrLineBox>(boxPoints.Count);
        var composed = new List<string?>(boxPoints.Count);
        for (var i = 0; i < boxPoints.Count && i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.IsNullOrEmpty(text?.Trim()))
            {
                continue;
            }

            var rect = BoundingRect(boxPoints[i], imageWidth, imageHeight);
            if (rect.IsEmpty)
            {
                continue;
            }

            composed.Add(text);
            // 单伪词行框：词框 = 行框 = 四点外接矩形（14.9.1，行框→块→原位替换管线零改动）
            var word = new OcrWordBox(text, rect);
            lines.Add(new OcrLineBox(lines.Count, text, [word], rect));
        }

        return new OcrRecognition(
            OcrTextComposer.Compose(composed),
            lines,
            EstimateTextAngle(boxPoints, imageWidth, imageHeight),
            EngineTag: null);
    }
}
