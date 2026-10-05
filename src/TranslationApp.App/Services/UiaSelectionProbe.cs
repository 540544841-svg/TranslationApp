using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using Serilog;
using TranslationApp.Core.SystemIntegration;

namespace TranslationApp.Services;

/// <summary>
/// 悬停取词的选区探针（FR-036）：用 UI Automation 问「抬起的地方到底有没有选中文字」。
/// 这是浮标出现的唯一依据——用户要的是「选中文字才浮出印标」，而不是「鼠标动过就浮出印标」。
/// <para>
/// 四条纪律，缺一条都会砸到用户手上：
/// ① 绝不能阻塞钩子线程：UIA 是跨进程调用，目标应用不响应时会**一直**不返回
/// （实测：窗口不跑消息泵时 <see cref="AutomationElement.FromPoint"/> 永久挂起），
/// 所以整段探测跑在线程池上，并且带硬预算，超时按 Unknown 处理（本次不落印）。
/// ② 抬起后要等一下再问：选区是目标应用在抬起之后才提交的，立刻读会读到上一轮的旧选区。
/// ③ 判据必须是「有没有非空文本」而不是「返回了几段」：Chromium 的空选区也返回一段，只是那段文本是空的。
/// ④ 有选区还不够，还得确认它就在落点附近（见 <see cref="SelectionGeometry"/>）：
/// 同一文档里可能留着上一步操作的旧选区，拖滚动条时不能算到这次头上。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiaSelectionProbe : ISelectionProbe
{
    /// <summary>抬起后先等这么久再问（给目标应用提交选区的时间）。</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// 单次询问的硬预算，超过就按 Unknown 办（＝这次不落印）。
    /// 本机实测：热问 40~70ms，进程内首问 ~120ms，浏览器刚打开 / 无障碍树正在建时能到 350ms 以上。
    /// 宁可等久一点，也不要因为「问慢了」就把印标吞掉——超时在用户眼里就是「功能坏了」。
    /// 上限卡在 600ms：再长就不如让用户直接按 Alt+S 了。
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// 向上找文本接口的层数上限。Chromium 把 TextPattern 挂在 Document 上，
    /// 从命中的 div 往上要走 5 层，所以留到 8。
    /// </summary>
    private const int AncestorDepth = 8;

    public async Task<SelectionProbeResult> ProbeAsync(SelectionProbeRequest request)
    {
        try
        {
            await Task.Delay(Settle).ConfigureAwait(false);
            var probing = Task.Run(() => Probe(request));
            return await probing.WaitAsync(Budget).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Debug("悬停取词：选区探针超时（{Budget}ms），本次不落印", (int)Budget.TotalMilliseconds);
            return SelectionProbeResult.Unknown;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "悬停取词：选区探针失败，本次不落印");
            return SelectionProbeResult.Unknown;
        }
    }

    private static SelectionProbeResult Probe(SelectionProbeRequest request)
    {
        var up = Read(request.UpX, request.UpY);
        if (up.HasSelection && SelectionGeometry.IsNear(request.UpX, request.UpY, up.Rects))
        {
            return SelectionProbeResult.Selected;
        }

        // 从右往左拖选时抬起点可能落在文本之外，按下点再补一刀
        var down = Read(request.DownX, request.DownY);
        if (down.HasSelection && SelectionGeometry.IsNear(request.DownX, request.DownY, down.Rects))
        {
            return SelectionProbeResult.Selected;
        }

        // 走到这里只有两种可能：确实没选中，或者选中的是别处留下的一段（跟这次操作无关）——都不落印。
        // 两个点都问不出来时才交回 Unknown。
        return up.TextFound || down.TextFound ? SelectionProbeResult.None : SelectionProbeResult.Unknown;
    }

    /// <summary>问一个点：自下而上找到第一个暴露文本接口的元素，用它的选区下结论。</summary>
    private static PointReading Read(int x, int y)
    {
        try
        {
            var node = AutomationElement.FromPoint(new Point(x, y));
            for (var depth = 0; node is not null && depth < AncestorDepth; depth++)
            {
                if (TryReadSelection(node, out var reading))
                {
                    return reading;
                }

                node = TreeWalker.ControlViewWalker.GetParent(node);
            }

            return default;
        }
        catch
        {
            return default; // UIPI 拦截 / 应用已退出 / 没有可用的 UIA 提供程序
        }
    }

    /// <summary>
    /// 这一层元素暴露文本接口吗？暴露了就用它的选区下结论。
    /// </summary>
    private static bool TryReadSelection(AutomationElement node, out PointReading reading)
    {
        reading = default;
        try
        {
            if (!node.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern text)
            {
                return false;
            }

            foreach (var range in text.GetSelection())
            {
                if (string.IsNullOrWhiteSpace(range.GetText(-1)))
                {
                    continue;
                }

                reading = new PointReading(true, true, Flatten(range.GetBoundingRectangles()));
                return true;
            }

            reading = new PointReading(true, false, null); // 问到了：这一段文本里没有选中任何东西
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// UIA 给的是 Rect[]，Core 的几何判断只认 [x, y, w, h] × n 的扁平数组
    /// （Core 不该认识 System.Windows.Rect——那是 UI 层的类型）。
    /// </summary>
    private static double[] Flatten(Rect[] rects)
    {
        var flat = new double[rects.Length * 4];
        for (var i = 0; i < rects.Length; i++)
        {
            flat[(i * 4) + 0] = rects[i].X;
            flat[(i * 4) + 1] = rects[i].Y;
            flat[(i * 4) + 2] = rects[i].Width;
            flat[(i * 4) + 3] = rects[i].Height;
        }

        return flat;
    }

    /// <summary>一个落点问出来的结果。</summary>
    /// <param name="TextFound">问到了文本接口（区别于「问不出来」）。</param>
    /// <param name="HasSelection">这段文本里有非空选区。</param>
    /// <param name="Rects">选区的屏幕物理矩形，提供程序不给时为 null。</param>
    private readonly record struct PointReading(bool TextFound, bool HasSelection, IReadOnlyList<double>? Rects);
}
