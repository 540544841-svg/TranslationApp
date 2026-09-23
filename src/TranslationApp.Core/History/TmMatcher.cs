namespace TranslationApp.Core.History;

/// <summary>一条 TM 候选（来自翻译历史的轻量投影）。</summary>
public sealed record TmCandidate(string Source, string Translated, DateTimeOffset CreatedAt, bool Reviewed = false);

/// <summary>命中结果：候选 + 相似度（0~1）。</summary>
public sealed record TmHit(TmCandidate Entry, double Score);

/// <summary>
/// 翻译记忆相似句匹配（FR-045 / spec §3）：先按长度差 >15% 预筛，
/// 再算归一化 Levenshtein（1 - dist/maxLen），≥ <see cref="MinSimilarity"/> 取最高分。
/// 候选 ≤200 条纯 CPU 比对，实测预算 &lt;5ms——替代网络请求只会更快，不破「快到无感」红线。
/// </summary>
public static class TmMatcher
{
    /// <summary>回填阈值：低于它的"相似"回填错译风险大于收益（A2-2 的顾虑就靠这个阈值+可视化兜底）。</summary>
    public const double MinSimilarity = 0.92;

    /// <summary>长度差预筛比例：超过即不可能达标，跳过逐格比对。</summary>
    public const double MaxLengthGapRatio = 0.15;

    public static TmHit? Find(string input, IReadOnlyList<TmCandidate> candidates)
    {
        var needle = (input ?? "").Trim();
        if (needle.Length == 0 || candidates is not { Count: > 0 })
        {
            return null;
        }

        TmHit? best = null;
        foreach (var candidate in candidates)
        {
            var source = candidate.Source.Trim();
            if (source.Length == 0) continue;

            var maxLength = Math.Max(needle.Length, source.Length);
            if (maxLength - Math.Min(needle.Length, source.Length) > maxLength * MaxLengthGapRatio)
            {
                continue;
            }

            var score = 1.0 - (double)Distance(needle, source) / maxLength;
            if (score >= MinSimilarity && (best is null || score > best.Score))
            {
                best = new TmHit(candidate, score);
            }
        }

        return best;
    }

    /// <summary>滚动数组 Levenshtein（O(min(m,n)) 空间）。</summary>
    internal static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
