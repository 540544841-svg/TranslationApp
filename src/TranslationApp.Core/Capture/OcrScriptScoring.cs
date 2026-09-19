namespace TranslationApp.Core.Capture;

/// <summary>
/// OCR「脚本合理性」打分（13.2.5 C-⑥：auto 双跑择优）。
///
/// 背景：中文系统上 <c>auto</c> = 配置文件语言引擎（zh-Hans-CN），英文文本会被识别成 CJK 乱码，
/// 乱码又被当作 zh→zh 直通显示。修法：auto 时配置文件语言引擎与 en-US 引擎各识别一次，
/// 按**脚本合理性**择优——纯函数放 Core 以便穷举单测，WinRT 双跑留在 App 层 <c>OcrService</c>。
///
/// 打分语义：<see cref="Score"/> 正值偏拉丁文本（成词拉丁字母 + 常规标点占比高），
/// 负值偏 CJK（汉字/假名/谚文占比高）；空串、纯数字等无脚本特征的内容为 0（中性）。
/// 择优（<see cref="ShouldPreferSecondary"/>）刻意**偏保守**：候选必须"像成段文字"且
/// 内容量与主候选相当才允许推翻配置文件语言引擎，两者接近时保持配置文件语言引擎——
/// 宁可维持现状，不可把中文界面截图的零散英文碎片误判成"英文更优"。
/// </summary>
public static class OcrScriptScoring
{
    /// <summary>择优得分差阈值：候选领先不足该值视为"接近"，保持配置文件语言引擎。</summary>
    public const double Epsilon = 0.15;

    /// <summary>候选参与择优所需的最低"成段文字特征"量（2+ 字母单词字符 + CJK 字符）。</summary>
    public const int MinSubstantialChars = 6;

    /// <summary>候选内容量下限（相对主候选）：低于该比例说明候选引擎只认出了零散碎片，不参与择优。
    /// 仅在词框面积不可得时的**回退口径**（见 <see cref="ShouldPreferSecondary"/>）。</summary>
    public const double MinCoverageRatio = 0.8;

    /// <summary>
    /// 词框总面积覆盖率下限（相对主候选）：条件②的现行口径。
    /// 几何量对「乱码更长」免疫——zh 引擎对英文的 CJK 乱码输出往往比英文原文更**长**（字符数更多），
    /// 用字符数比较会让门控失效；而两个引擎看到的是同一张图，识别正确时词框总面积必然接近。
    /// </summary>
    public const double MinAreaCoverageRatio = 0.6;

    /// <summary>各字符类的权重（非空格字符的归一化得分）。</summary>
    private const double WordCharWeight = 1.0;    // 2+ 字母单词中的拉丁字母（有词结构，英文强特征）
    private const double LoneLetterWeight = 0.1;  // 孤立单字母（中文截图的英文碎片多为单字母，几乎不加分）
    private const double AsciiPunctWeight = 0.3;  // 常规 ASCII 标点（英文伴随特征）
    private const double CjkWeight = -1.0;        // CJK 字符与全角标点（中文强特征）

    /// <summary>
    /// 脚本冲突逃生口（14.3.12.8）的「拉丁方向」判定线：与 <see cref="OcrLanguages.ScriptConflictThreshold"/>
    /// 同值同义（0.5）——正是 <c>ReconcileWithScript</c> 判「文本实为拉丁」的同一口径，直接引用该常量保证永不漂移。
    /// </summary>
    public const double EscapeScoreThreshold = OcrLanguages.ScriptConflictThreshold;

    /// <summary>主候选混入 CJK 杂质的计数下限：≥ 2 个 CJK 才构成「zh 误读拉丁文本」的结构签名（1 个视为噪声）。</summary>
    public const int EscapeMinCjkChars = 2;

    /// <summary>
    /// 脚本合理性得分：正值偏拉丁文本、负值偏 CJK 文本；空串 / 纯数字 / 无字符特征返回 0（中性）。
    /// 数字计为中性（中英文场景同样常见，不参与方向判定）。
    /// </summary>
    public static double Score(string? text) => ScoreOf(Count(text));

    /// <summary>得分的纯计算核心（字符统计 → 归一化得分），供 <see cref="IsScriptConflictEscape"/> 复用统计避免重复扫描。</summary>
    private static double ScoreOf(ScriptStats stats) => stats.Total == 0
        ? 0
        : (stats.WordChars * WordCharWeight
           + stats.LoneLetters * LoneLetterWeight
           + stats.AsciiPunct * AsciiPunctWeight
           + stats.Cjk * CjkWeight)
          / stats.Total;

    /// <summary>
    /// 双跑择优判定（两参数版，保留给既有测试）：面积缺省时条件②回退旧的 Content 字符数比例。
    /// </summary>
    public static bool ShouldPreferSecondary(string? primary, string? secondary) =>
        ShouldPreferSecondary(primary, secondary, double.NaN, double.NaN);

    /// <summary>
    /// 双跑择优判定：secondary（en-US 引擎候选）是否应胜过 primary（配置文件语言引擎候选）。
    /// 三个条件全部满足才切换——① 候选自身"像成段文字"（空串/纯数字/零散单字符不参与）；
    /// ② 候选**词框总面积**达到主候选的 <see cref="MinAreaCoverageRatio"/>（几何量对乱码长度免疫；
    /// 面积不可得（≤0）时回退旧的 Content 字符数比例 <see cref="MinCoverageRatio"/>，防中文界面截图被零散英文碎片推翻）；
    /// ③ 得分领先超过 <see cref="Epsilon"/>（两者接近时保持配置文件语言引擎），
    /// 或命中脚本冲突逃生口 <see cref="IsScriptConflictEscape"/>（14.3.12.8；①②不可豁免）。
    /// </summary>
    /// <param name="secondaryBoxArea">secondary 的词框总面积（Σ word.BoundingRect 面积，图像像素²）。</param>
    /// <param name="primaryBoxArea">primary 的词框总面积。</param>
    public static bool ShouldPreferSecondary(
        string? primary, string? secondary, double secondaryBoxArea, double primaryBoxArea)
    {
        var s = Count(secondary);
        if (s.Substantial < MinSubstantialChars)
        {
            return false; // ①
        }

        var p = Count(primary);
        if (!HasEnoughCoverage(s.Content, p.Content, secondaryBoxArea, primaryBoxArea))
        {
            return false; // ②
        }

        return Score(secondary) - Score(primary) > Epsilon // ③
               || IsScriptConflictEscape(primary, secondary); // ③ 的逃生口豁免分支（14.3.12.8）
    }

    /// <summary>
    /// 脚本冲突逃生口（14.3.12.8）：条件①②已通过的前提下，免除条件③的 0.15 领先要求。
    /// 结构签名 =「主候选拉丁为主、混入 CJK 杂质（E1）」+「次候选纯拉丁成段（E2）」——
    /// 主候选文本已被判为拉丁方向（得分过 <see cref="EscapeScoreThreshold"/>）却带 ≥ <see cref="EscapeMinCjkChars"/> 个 CJK，
    /// 正是 zh 引擎误读拉丁文本的形态；领先幅度随图像渲染不可靠（同内容族 0.358 vs 0.077），结构性特征才可靠。
    /// 依据与「把 Epsilon 降到 0.05」「拉丁词平均词长」两替代方案均被探针实测否决的结论（见需求文档 14.3.12.8）。
    /// </summary>
    private static bool IsScriptConflictEscape(string? primary, string? secondary)
    {
        // E1：主候选整文已过「拉丁方向」线，却混入 ≥ 2 个 CJK（拉丁为主的杂质）
        var p = Count(primary);
        if (p.Cjk < EscapeMinCjkChars || ScoreOf(p) <= EscapeScoreThreshold)
        {
            return false;
        }

        // E2：次候选纯拉丁成段（无 CJK）且自身也过「拉丁方向」线
        var s = Count(secondary);
        return s.Cjk == 0 && ScoreOf(s) > EscapeScoreThreshold;
    }

    /// <summary>条件②：优先用词框总面积比较；面积缺省时回退 Content 字符数比例（旧行为，保持兼容）。</summary>
    private static bool HasEnoughCoverage(
        int secondaryContent, int primaryContent, double secondaryArea, double primaryArea) =>
        secondaryArea > 0 && primaryArea > 0
            ? secondaryArea >= primaryArea * MinAreaCoverageRatio
            : secondaryContent >= primaryContent * MinCoverageRatio;

    /// <summary>识别文本的内容字符数（Content = 有词结构的拉丁字符 + 孤立字母 + CJK），双跑决策日志用（不含内容本身）。</summary>
    public static int ContentChars(string? text) => Count(text).Content;

    /// <summary>
    /// 双跑决策的命中条件描述（日志用，不含识别内容）：
    /// 返回「全部条件命中」或第一个未命中的条件，让「为什么保持 / 为什么切换」能直接从日志读出。
    /// </summary>
    public static string DescribeDecision(
        string? primary, string? secondary, double secondaryBoxArea, double primaryBoxArea)
    {
        var s = Count(secondary);
        if (s.Substantial < MinSubstantialChars)
        {
            return "条件①未命中（候选无成段文字特征）";
        }

        var p = Count(primary);
        if (!HasEnoughCoverage(s.Content, p.Content, secondaryBoxArea, primaryBoxArea))
        {
            return secondaryBoxArea > 0 && primaryBoxArea > 0
                ? "条件②未命中（候选词框面积不足）"
                : "条件②未命中（候选字符数不足）";
        }

        if (Score(secondary) - Score(primary) > Epsilon)
        {
            return "全部条件命中";
        }

        return IsScriptConflictEscape(primary, secondary)
            ? "全部条件命中（③ 经脚本冲突逃生口豁免：主候选拉丁为主混入 CJK）"
            : "条件③未命中（得分接近）";
    }

    /// <summary>字符统计（内部）。Substantial = 有词结构的拉丁字符 + CJK；Content = 两者 + 孤立字母。</summary>
    private readonly record struct ScriptStats(
        int WordChars, int LoneLetters, int AsciiPunct, int Cjk, int Total)
    {
        public int Substantial => WordChars + Cjk;
        public int Content => WordChars + LoneLetters + Cjk;
    }

    private static ScriptStats Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        int wordChars = 0, loneLetters = 0, asciiPunct = 0, cjk = 0, total = 0;
        var inWord = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false; // 词边界
                continue;
            }

            total++;
            if (IsCjk(ch))
            {
                cjk++;
                inWord = false;
            }
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
            {
                if (inWord)
                {
                    wordChars++;
                }
                else
                {
                    loneLetters++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
                if (ch is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                {
                    asciiPunct++;
                }
                // 其余（数字、符号等）计入 Total 但权重为 0：数字保持中性
            }
        }

        return new ScriptStats(wordChars, loneLetters, asciiPunct, cjk, total);
    }

    /// <summary>CJK 判定：汉字（含扩展 A/兼容区）、假名、谚文、CJK 标点与全角形式。</summary>
    private static bool IsCjk(char ch) =>
        ch is >= '\u2E80' and <= '\u9FFF'   // 部首补充 ~ 统一表意文字（含 CJK 标点、假名、注音、谚文兼容、扩展 A）
        or >= '\uAC00' and <= '\uD7AF'      // 谚文音节
        or >= '\uF900' and <= '\uFAFF'      // 兼容表意文字
        or >= '\uFF00' and <= '\uFFEF';     // 全角形式与半角片假名
}
