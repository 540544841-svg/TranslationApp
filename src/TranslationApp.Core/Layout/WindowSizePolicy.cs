namespace TranslationApp.Core.Layout;

/// <summary>
/// 翻译小窗尺寸策略（FR-026 / 14.2）——纯函数、无 WPF 依赖，可穷举单测。
///
/// <b>坐标系</b>：本类所有尺寸均为「窗口 DIP 尺寸」，即**含阴影留白**、与设置项
/// <c>QuickWindowWidth/Height</c> 完全同一坐标系（调用方若需要「卡片意图尺寸」自行减去留白）。
///
/// <b>尺寸语义（阶段 5 批 3 改版）</b>：
/// ① **基准永远是设置里的默认宽高**，不是上次拖拽后的尺寸——每次呼出都回到默认值；
/// ② 按内容自适应时只按内容**增大**：内容少时不得缩到比默认值更小（默认值即用户偏好）；
/// ③ 宽度按内容所需增长，**上限 640 DIP**；但默认值本身超过 640 时以默认值为准（640 只约束"加宽"这个动作）；
/// ④ 高度上限沿用 <c>min(0.80 × 工作区高, 640)</c>，超长文本由译文区内部滚动兜底；
/// ⑤ 自适应关闭 = 严格使用默认宽高，不做任何内容测量；
/// ⑥ 手动拖拽**只影响本次窗口**，由调用方保证绝不写回设置（本类不感知持久化）。
/// </summary>
public static class WindowSizePolicy
{
    // ==================== 尺寸模式（沿用既有设置字段 QuickWindowSizeMode） ====================

    /// <summary>按内容自适应（默认）。</summary>
    public const string AutoMode = "auto";

    /// <summary>固定尺寸：严格使用设置里的默认宽高，不做内容测量。<b>不再由拖拽进入</b>（拖拽不跨次保留）。</summary>
    public const string ManualMode = "manual";

    // ==================== 取值范围与默认值（14.2.3 / 14.6） ====================

    /// <summary>默认宽度允许范围下限（DIP）。</summary>
    public const double MinWidthDip = 320;

    /// <summary>默认宽度允许范围上限（DIP）。</summary>
    public const double MaxWidthDip = 900;

    /// <summary>默认高度允许范围下限（DIP）。</summary>
    public const double MinHeightDip = 240;

    /// <summary>默认高度允许范围上限（DIP）。</summary>
    public const double MaxHeightDip = 900;

    /// <summary>推荐默认宽度（DIP）：「恢复推荐默认值」按钮的重置目标。</summary>
    public const double DefaultWidthDip = 420;

    /// <summary>推荐默认高度（DIP）。</summary>
    public const double DefaultHeightDip = 320;

    /// <summary>
    /// 按内容加宽的绝对上限（DIP）。**只约束"自适应加宽"这个动作**：
    /// 默认宽度本身大于该值时直接使用默认宽度，自适应不再加宽。
    /// </summary>
    public const double AdaptiveWidthCapDip = 640;

    /// <summary>内容高度绝对上限（DIP）：防超高分屏下窗口过大（14.2.3）。</summary>
    public const double MaxHeightCapDip = 640;

    /// <summary>内容高度相对上限：工作区高的 0.80，留出任务栏与上下文可视空间（14.2.3）。</summary>
    public const double WorkAreaHeightRatio = 0.80;

    // ==================== 模式判定 ====================

    /// <summary>
    /// 是否为固定尺寸（不随内容自适应）。未知值、null 一律视为默认的自适应模式。
    /// 值名沿用 <c>manual</c> 以兼容已落盘的配置。
    /// </summary>
    public static bool IsManual(string? mode) =>
        string.Equals(mode, ManualMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>把设置里的模式串归一化为 <see cref="AutoMode"/> / <see cref="ManualMode"/>。</summary>
    public static string NormalizeMode(string? mode) => IsManual(mode) ? ManualMode : AutoMode;

    // ==================== 设置值夹取 ====================

    /// <summary>把默认宽度夹取到 <c>[320, 900]</c>；NaN（配置损坏）回退到推荐默认值 420。</summary>
    public static double ClampWidth(double widthDip) =>
        double.IsNaN(widthDip) ? DefaultWidthDip : Math.Clamp(widthDip, MinWidthDip, MaxWidthDip);

    /// <summary>把默认高度夹取到 <c>[240, 900]</c>；NaN（配置损坏）回退到推荐默认值 320。</summary>
    public static double ClampHeight(double heightDip) =>
        double.IsNaN(heightDip) ? DefaultHeightDip : Math.Clamp(heightDip, MinHeightDip, MaxHeightDip);

    // ==================== 内容上限 ====================

    /// <summary>
    /// 内容高度上限 = <c>min(0.80 × 工作区高, 640)</c>；工作区高度不可知（≤ 0）时只用绝对上限。
    /// 例（14.2.3）：1366×768 屏上 0.80 × 768 ≈ 614 → 取 614；1080p 屏上 0.80 × 1032 ≈ 826 → 取 640。
    /// </summary>
    public static double MaxContentHeightDip(double workAreaHeightDip) =>
        workAreaHeightDip <= 0
            ? MaxHeightCapDip
            : Math.Min(MaxHeightCapDip, WorkAreaHeightRatio * workAreaHeightDip);

    /// <summary>
    /// 内容加宽上限 = <c>max(默认宽度, 640)</c>——默认值可越过 640（见 <see cref="AdaptiveWidthCapDip"/>）。
    /// </summary>
    public static double MaxContentWidthDip(double defaultWidthDip) =>
        Math.Max(ClampWidth(defaultWidthDip), AdaptiveWidthCapDip);

    /// <summary>
    /// 宽度加宽的收敛目标行数：内容能在这个行数内排完就不再继续加宽。
    /// 5 行时译文少于约 145 个中文字符永不加宽、首次加宽仅 +10 DIP 不可感知；
    /// 取 3 行后 100 字即可感知加宽（约 +64 DIP）、150 字到 640 封顶、短文本仍保持默认宽度。
    /// </summary>
    public const int AdaptiveWidthTargetLines = 3;

    /// <summary>
    /// 把「不限宽排版宽度」折算成「目标行数内排完所需的宽度」。
    ///
    /// 为什么不直接用不限宽宽度：那等于"完全不折行所需宽度"，中文约 50 字就会撞到 640 上限，
    /// 观感上变成「420 或 640」两档跳，失去自适应的意义。
    /// 按目标行数折算后，宽度随内容量渐进增长：短文本保持默认宽度、中等文本小幅加宽、很长才到上限。
    /// </summary>
    /// <param name="unwrappedWidthDip">不限宽排版时的单行宽度（DIP，含内边距）。</param>
    /// <param name="targetLines">目标行数，默认 <see cref="AdaptiveWidthTargetLines"/>。</param>
    public static double RequiredWidthForLineTarget(
        double unwrappedWidthDip, int targetLines = AdaptiveWidthTargetLines)
    {
        if (double.IsNaN(unwrappedWidthDip) || unwrappedWidthDip <= 0 || targetLines < 1)
        {
            return 0;
        }

        return unwrappedWidthDip / targetLines;
    }

    /// <summary>把「内容所需高度」夹取到 <c>[min(240, 上限), 上限]</c>（14.2.3 的第一级夹取）。</summary>
    public static double ClampContentHeight(double neededContentHeightDip, double workAreaHeightDip)
    {
        var max = MaxContentHeightDip(workAreaHeightDip);
        var min = Math.Min(MinHeightDip, max); // 工作区比最小高度还矮时以工作区为准，避免 min > max 抛异常
        return double.IsNaN(neededContentHeightDip) ? min : Math.Clamp(neededContentHeightDip, min, max);
    }

    // ==================== 最终尺寸 ====================

    /// <summary>
    /// 最终宽度：基准 = 默认宽度（<b>不是</b>上次拖拽的宽度）。
    /// 自适应开启时按内容所需加宽、封顶 <c>max(默认宽度, 640)</c>；**只增不减**（内容少时保持默认宽度）；
    /// 测量失败（NaN / ≤ 0）时保持默认宽度，不缩小、不抛异常。
    /// 自适应关闭时直接返回默认宽度，不做任何内容相关计算。
    /// </summary>
    /// <param name="defaultWidthDip">设置里的默认宽度（DIP，含阴影留白）。</param>
    /// <param name="neededContentWidthDip">测量出的「内容所需宽度」；不可知传 0 或 NaN。</param>
    /// <param name="adaptToContent">是否按内容自适应（= 设置开关）。</param>
    public static double ResolveWidth(double defaultWidthDip, double neededContentWidthDip, bool adaptToContent)
    {
        var baseline = ClampWidth(defaultWidthDip);
        if (!adaptToContent || double.IsNaN(neededContentWidthDip) || neededContentWidthDip <= 0)
        {
            return baseline;
        }

        var grown = Math.Max(baseline, neededContentWidthDip);
        return Math.Min(grown, MaxContentWidthDip(baseline));
    }

    /// <summary>
    /// 最终高度：基准 = 默认高度（<b>不是</b>上次拖拽或上次会话的高度）。
    /// 自适应开启时按内容所需高度增长并夹取，**只增不减**；关闭时直接返回默认高度。
    /// </summary>
    /// <param name="defaultHeightDip">设置里的默认高度（DIP，含阴影留白）。</param>
    /// <param name="neededContentHeightDip">测量出的「内容所需高度」；不可知传 0 或 NaN。</param>
    /// <param name="sessionHeightDip">本次会话（一次呼出到一次隐藏）已到达的高度，用于会话内单调不减；呼出时传默认高度。</param>
    /// <param name="adaptToContent">是否按内容自适应（= 设置开关）。</param>
    /// <param name="workAreaHeightDip">鼠标所在屏工作区高度（DIP）；未知传 0。</param>
    public static double ResolveHeight(
        double defaultHeightDip,
        double neededContentHeightDip,
        double sessionHeightDip,
        bool adaptToContent,
        double workAreaHeightDip)
    {
        var baseline = ClampHeight(defaultHeightDip);
        if (!adaptToContent)
        {
            return baseline;
        }

        // 会话内单调不减的基线：内容变短也不回缩；呼出时等于默认高度，因此不会把上次会话的高度带过来
        var floor = Math.Max(baseline, ClampHeight(sessionHeightDip));
        if (double.IsNaN(neededContentHeightDip) || neededContentHeightDip <= 0)
        {
            return floor; // 测量失败：保持当前尺寸，不缩小、不抛异常
        }

        var byContent = ClampContentHeight(neededContentHeightDip, workAreaHeightDip);
        return ClampHeight(Math.Max(floor, byContent)); // 只增不减：默认值可越过内容上限（用户偏好优先）
    }

    /// <summary>
    /// 「把当前尺寸设为默认」时写入设置的值：按设置取值范围夹取（<c>[320,900] × [240,900]</c>），
    /// 保留一位小数。两个方向都独立夹取，另一方向超范围不影响本方向。
    /// </summary>
    public static (double Width, double Height) NormalizeAsDefault(double windowWidthDip, double windowHeightDip) =>
        (Math.Round(ClampWidth(windowWidthDip), 1), Math.Round(ClampHeight(windowHeightDip), 1));
}
