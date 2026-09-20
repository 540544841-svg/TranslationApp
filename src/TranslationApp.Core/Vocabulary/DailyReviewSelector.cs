namespace TranslationApp.Core.Vocabulary;

/// <summary>
/// 每日复习选题（FR-052 / 批 5 spec §3）：**纯轮转**——按「距 epoch 的天数」定今天的起点，
/// 同一天稳定同一批词、跨天自然往后挪，不引入熟悉度/遗忘曲线（那是 B4 复习闭环的正题，
/// 需要新表与新交互，本功能只按 v2 文档要求做"最轻验证"）。
/// 纯函数、无 IO：生词列表由调用方按现有顺序传入。
/// </summary>
public static class DailyReviewSelector
{
    /// <summary>每天给几个复习词（v2 文档 B4-1 的"每日 5 词"）。</summary>
    public const int WordsPerDay = 5;

    /// <summary>轮转基准日：取项目起点附近一个固定日期，只要不变就行（改了等于重新洗牌）。</summary>
    private static readonly DateOnly Epoch = new(2026, 1, 1);

    /// <summary>今天的起始下标；<paramref name="totalWords"/> 为 0 时返回 0。</summary>
    public static int StartIndex(DateOnly day, int totalWords)
    {
        if (totalWords <= 0)
        {
            return 0;
        }

        // 系统时钟早于基准日时差值会是负数，C# 的 % 保留符号 → 归一到 [0, totalWords)
        var offset = ((long)day.DayNumber - Epoch.DayNumber) * WordsPerDay % totalWords;
        return (int)((offset + totalWords) % totalWords);
    }

    /// <summary>
    /// 今天该看的词下标（≤<see cref="WordsPerDay"/> 个，按列表顺序环形取）。
    /// 生词不足 5 个时就是全部；空列表返回空。
    /// </summary>
    public static IReadOnlyList<int> Pick(DateOnly day, int totalWords)
    {
        if (totalWords <= 0)
        {
            return [];
        }

        var count = Math.Min(WordsPerDay, totalWords);
        var start = StartIndex(day, totalWords);
        var indices = new int[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = (start + i) % totalWords;
        }

        return indices;
    }
}
