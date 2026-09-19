using TranslationApp.Core.Capture;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// OCR 脚本合理性打分（C-⑥ auto 双跑择优）：英文样例得分须高于 zh 引擎对英文的典型乱码输出，
/// 中文样例反之由「保持配置文件语言引擎」的判定保证；空串/纯数字中性；
/// 择优判定偏保守（零散碎片、内容量不足、得分接近时一律保持配置文件语言引擎）。
/// </summary>
public sealed class OcrScriptScoringTests
{
    private const string EnglishSample = "Hello world, this is a song lyric line that we all know";

    /// <summary>zh-Hans-CN 引擎对英文歌词的典型输出：成串 CJK 乱码（用户报告的缺陷形态）。</summary>
    private const string ZhGarbageOnEnglish = "嗯嗯哦哦嗯嗯哦哦嗯嗯嗯哦哦嗯嗯哦嗯嗯哦哦嗯嗯";

    private const string ChineseSample = "今天天气真好，我们一起去公园散步吧。";

    /// <summary>en-US 引擎对中文文本的典型输出：零散的单字母/数字碎片，无词结构。</summary>
    private const string EnGarbageOnChinese = "T R 5 E b E 1 L";

    /// <summary>en-US 引擎对中文界面截图的典型输出：只认出界面里夹的零散英文词，内容量远少于中文全文。</summary>
    private const string EnFragmentsOnChineseUi = "Windows 10 OK Go";

    // ---------- 打分 ----------

    [Fact]
    public void Score_英文样例高于zh引擎乱码输出()
    {
        Assert.True(
            OcrScriptScoring.Score(EnglishSample) > OcrScriptScoring.Score(ZhGarbageOnEnglish),
            $"英文 {OcrScriptScoring.Score(EnglishSample):0.###} 应 > zh 乱码 {OcrScriptScoring.Score(ZhGarbageOnEnglish):0.###}");
        Assert.True(OcrScriptScoring.Score(EnglishSample) > 0);
        Assert.True(OcrScriptScoring.Score(ZhGarbageOnEnglish) < 0);
    }

    [Fact]
    public void Score_中文样例偏CJK为负()
    {
        Assert.True(OcrScriptScoring.Score(ChineseSample) < 0);
    }

    [Fact]
    public void Score_空串与纯数字中性()
    {
        Assert.Equal(0, OcrScriptScoring.Score(null), 6);
        Assert.Equal(0, OcrScriptScoring.Score(""), 6);
        Assert.Equal(0, OcrScriptScoring.Score("2026 09 14"), 6);
        Assert.Equal(0, OcrScriptScoring.Score("  \n\t "), 6);
    }

    // ---------- 择优判定 ----------

    [Fact]
    public void ShouldPreferSecondary_英文图片时en引擎胜出()
    {
        // 主候选 = zh 引擎输出的 CJK 乱码，次候选 = en 引擎输出的正确英文
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(ZhGarbageOnEnglish, EnglishSample));
    }

    [Fact]
    public void ShouldPreferSecondary_中文图片时保持配置文件语言引擎()
    {
        // 零散单字母碎片：无成段文字特征，直接不参与择优
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnGarbageOnChinese));

        // 有词结构但内容量远少于中文全文（只认出了界面里夹的英文词）：不允许推翻
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(
            "这是翻译软件的设置界面可以看到系统的语言与主题选项", EnFragmentsOnChineseUi));
    }

    [Fact]
    public void ShouldPreferSecondary_得分接近时保持配置文件语言引擎()
    {
        var primary = "hello world this is fine";
        var secondary = "hello world this is fine!";
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(primary, secondary));
    }

    [Fact]
    public void ShouldPreferSecondary_空串或纯数字永不推翻主候选()
    {
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ZhGarbageOnEnglish, null));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ZhGarbageOnEnglish, ""));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ZhGarbageOnEnglish, "12345 678"));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, "12345 678"));
    }

    // ---------- 择优判定：几何覆盖率（v1.2 修复批 ③-A） ----------

    /// <summary>英文短语（30 个内容字母，低于 40 字 CJK 乱码 × 0.8 的旧门控）。</summary>
    private const string ShortEnglish = "you are my sunshine my only sunshine";

    /// <summary>40 字的 CJK 乱码（zh 引擎对英文的更长输出，字符数门控骗不过几何量）。</summary>
    private static readonly string LongGarbage = new('嗯', 40);

    [Fact]
    public void ShouldPreferSecondary_乱码更长但词框面积相当时_按几何覆盖率判en胜出()
    {
        // 缺陷回归：旧 Content 门控（30 < 40×0.8）失败 → 保守保留 zh；几何覆盖率（面积相当）应判 en 胜出
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(LongGarbage, ShortEnglish));
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(LongGarbage, ShortEnglish, 1000, 1000));
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(LongGarbage, ShortEnglish, 600, 1000)); // 恰在 0.6 阈值上
    }

    [Fact]
    public void ShouldPreferSecondary_中文界面加en碎片且碎片面积不足时_保持配置文件语言引擎()
    {
        // 碎片面积 < 0.6 × 中文面积：不允许推翻（与旧字符数门控同向，防误判）
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnFragmentsOnChineseUi, 300, 1000));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnGarbageOnChinese, 300, 1000));

        // 反例：面积足够时几何门控放行，交由条件③（得分差）定夺 → en 正确胜出
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnglishSample, 1000, 1000));
    }

    [Fact]
    public void ShouldPreferSecondary_面积缺省时回退旧的Content比例()
    {
        // NaN（缺省）与 0 都视为面积不可得 → 回退旧门控：30 < 32 → 保持 zh
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(LongGarbage, ShortEnglish, double.NaN, double.NaN));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(LongGarbage, ShortEnglish, 0, 0));
        // 旧行为本身不回归
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(ZhGarbageOnEnglish, EnglishSample, double.NaN, double.NaN));
    }

    [Fact]
    public void DescribeDecision_指出命中或未命中的条件()
    {
        Assert.Equal("全部条件命中", OcrScriptScoring.DescribeDecision(LongGarbage, ShortEnglish, 1000, 1000));
        Assert.StartsWith("条件①", OcrScriptScoring.DescribeDecision(ChineseSample, EnGarbageOnChinese, 100, 1000));
        Assert.StartsWith("条件②", OcrScriptScoring.DescribeDecision(LongGarbage, ShortEnglish, 100, 1000));
        Assert.StartsWith("条件③", OcrScriptScoring.DescribeDecision(
            "hello world this is fine", "hello world this is fine!", 1000, 1000));
    }

    [Fact]
    public void ContentChars_统计内容字符数_供决策日志使用()
    {
        Assert.Equal(40, OcrScriptScoring.ContentChars(LongGarbage));
        Assert.Equal(0, OcrScriptScoring.ContentChars(null));
        Assert.Equal(0, OcrScriptScoring.ContentChars("2026 09 14"));
    }

    // ---------- 脚本冲突逃生口（14.3.12.8） ----------

    /// <summary>
    /// zh 引擎对斜体英文歌词的「拉丁为主、混入 CJK 杂质」乱码（探针 D:\ocrprobe\probe-dualrun 摘录形态）：
    /// Score ≈ 0.82（> 0.5，与 ReconcileWithScript 判「文本实为拉丁」同向）、CJK 计数 7（≥ 2）。
    /// </summary>
    private const string LatinShapedGarbageWithCjk =
        "SOI 1 司 ie over the rainb way 加 刀 once Someday upon 豆 看 where the clo troubles 方 lemon drops " +
        "w above the mey tops a where yo 刀";

    /// <summary>en-US 引擎对同一段斜体歌词的正确识别（纯拉丁成段，无 CJK）。</summary>
    private const string RealLyricLine =
        "Somewhere over the rainbow way up high, There's a land that I heard of once in a lullaby.";

    /// <summary>主候选拉丁形乱码混 1 个 CJK（噪声，不足以构成结构签名）。</summary>
    private const string LatinShapedWithSingleCjk = "Somewhere over the rainbow way up high 司";

    /// <summary>主候选拉丁形乱码混 2 个 CJK（恰好达到杂杂质计数下限）。</summary>
    private const string LatinShapedWithTwoCjk = "Somewhere over the rainbow way up high 司 刀";

    [Fact]
    public void ShouldPreferSecondary_斜体歌词拉丁形乱码混CJK_逃生口豁免条件3判en胜出()
    {
        // 前置自检：主候选确为「拉丁为主混 CJK」（E1），次候选纯拉丁（E2），且得分领先 < 0.15（③不成立）
        Assert.True(OcrScriptScoring.Score(LatinShapedGarbageWithCjk) > OcrScriptScoring.EscapeScoreThreshold,
            $"primary 得分 {OcrScriptScoring.Score(LatinShapedGarbageWithCjk):0.###} 应 > 0.5");
        Assert.True(OcrScriptScoring.Score(RealLyricLine) - OcrScriptScoring.Score(LatinShapedGarbageWithCjk)
                    <= OcrScriptScoring.Epsilon,
            "领先应 ≤ 0.15（否则命中的是条件③而非逃生口）");

        // 词框面积相当（同一张图）：①②通过、③未命中 → 逃生口放行 en
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(
            LatinShapedGarbageWithCjk, RealLyricLine, 1000, 1000));
        // 决策日志给出逃生口分支文案（线上归因用）
        Assert.Equal(
            "全部条件命中（③ 经脚本冲突逃生口豁免：主候选拉丁为主混入 CJK）",
            OcrScriptScoring.DescribeDecision(LatinShapedGarbageWithCjk, RealLyricLine, 1000, 1000));
    }

    [Fact]
    public void ShouldPreferSecondary_斜体歌词回归_两参数旧口径下仍被字符数门控拦截()
    {
        // 两参数版（面积缺省回退 Content 比例）：歌词 69 字 < 乱码 95 字 × 0.8 → 条件②回退口径拦截；
        // 逃生口只能在①②通过后生效，不得越过字符数门控（四参数几何口径才判 en 胜出，见上一用例）
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(LatinShapedGarbageWithCjk, RealLyricLine));
    }

    [Fact]
    public void ShouldPreferSecondary_逃生口不误伤中文界面加英文碎片()
    {
        // 保护用例（文档 14.3.12.8）：主候选 CJK 主导 → E1 不满足（Score ≤ 0.5），逃生口绝不参与；
        // en 碎片无词结构 → 条件①拦截，面积充足与不足均不胜出
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnGarbageOnChinese, 1000, 1000));
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnGarbageOnChinese, 300, 1000));

        // en 碎片有词结构但面积不足 → 条件②拦截（双层保护的实际生效层）
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(ChineseSample, EnFragmentsOnChineseUi, 300, 1000));

        // 面积充足时 en 胜出只经条件③（既有行为），决策日志不含逃生口文案——逃生口没有新增翻转面
        Assert.Equal(
            "全部条件命中",
            OcrScriptScoring.DescribeDecision(ChineseSample, EnFragmentsOnChineseUi, 1000, 1000));
    }

    [Fact]
    public void ShouldPreferSecondary_主候选仅1个CJK噪声_逃生口不触发()
    {
        // E1 要求 Cjk ≥ 2：1 个 CJK 视为噪声；得分接近（③未命中）→ 保持配置文件语言引擎
        Assert.True(OcrScriptScoring.Score(LatinShapedWithSingleCjk) > OcrScriptScoring.EscapeScoreThreshold);
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(
            LatinShapedWithSingleCjk, RealLyricLine, 1000, 1000));

        // 同形态但 CJK 恰好 2 个 → 逃生口生效（钉死阈值 = 2 的边界）
        Assert.True(OcrScriptScoring.ShouldPreferSecondary(
            LatinShapedWithTwoCjk, RealLyricLine, 1000, 1000));
    }

    [Fact]
    public void ShouldPreferSecondary_次候选含CJK或低分_逃生口不触发()
    {
        // E2 要求次候选无 CJK：混入 1 个 CJK 即不构成「纯拉丁成段」
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(
            LatinShapedWithTwoCjk, "Somewhere over the rainbow way up high 刀", 1000, 1000));

        // E2 要求次候选得分 > 0.5：CJK 乱码次候选不触发
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(
            LatinShapedWithTwoCjk, "嗯嗯哦哦嗯嗯哦哦嗯嗯嗯哦哦嗯嗯哦嗯嗯哦哦嗯嗯", 1000, 1000));
    }

    [Fact]
    public void ShouldPreferSecondary_逃生口不豁免条件1_零散碎片仍被拦截()
    {
        // 条件①不可豁免：次候选无成段文字特征（abcde=5 词字符 < 6）时，即使 E1/E2 全部成立也不切换
        Assert.True(OcrScriptScoring.Score("abcde f") > OcrScriptScoring.EscapeScoreThreshold);
        Assert.False(OcrScriptScoring.ShouldPreferSecondary(LatinShapedWithTwoCjk, "abcde f", 1000, 1000));
    }
}
