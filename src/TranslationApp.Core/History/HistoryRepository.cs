using Dapper;

namespace TranslationApp.Core.History;

/// <summary>历史记录仓储（FR-014）。</summary>
public interface IHistoryRepository
{
    /// <summary>写入一条翻译记录，并自动清理超出上限的旧记录。</summary>
    void Add(string sourceText, string translatedText, string sourceLanguage, string targetLanguage, string engine);

    /// <summary>固定/取消固定一条历史；固定记录不受自动清理影响。</summary>
    void SetPinned(long id, bool pinned);

    /// <summary>按关键字与过滤条件搜索；关键字为空时返回最近的记录；固定记录优先。</summary>
    IReadOnlyList<TranslationRecord> Search(HistoryQuery query);

    /// <summary>
    /// TM 候选（FR-045 / spec §3）：同目标语言最近 limit 条的轻量投影，供 <see cref="TmMatcher"/> 比对。
    /// 只按目标语言过滤（源语言可能从 auto 检测得到，交由相似度兜底）。
    /// </summary>
    IReadOnlyList<TmCandidate> TmCandidates(string targetLanguage, int limit = 200);

    /// <summary>
    /// AI 语境化翻译的语境来源（FR-050）：同目标语言、<paramref name="maxAgeMinutes"/> 分钟内、
    /// 与当前输入不同最近一条原文；没有则返回 null（不携带语境）。
    /// </summary>
    string? ContextSource(string targetLanguage, string currentSource, int maxAgeMinutes = 30);

    void Delete(long id);

    /// <summary>编辑 TM 译文并更新校对/禁用状态；返回受影响行数。</summary>
    int Update(long id, string translatedText, bool reviewed, bool rejected);

    /// <summary>清空全部历史（FR-014「一键清空」）。</summary>
    void Clear();

    int Count();
}

/// <summary>
/// SQLite 实现。数据库不可用时所有方法安全返回（返回空列表/不抛异常），
/// 保证历史功能故障不会影响翻译主流程。
/// </summary>
public sealed class HistoryRepository : IHistoryRepository
{
    /// <summary>历史记录上限（FR-014：仅保留最近 5000 条）。</summary>
    public const int MaxRecords = 5000;

    /// <summary>取语境时探测的最近条数（跳过「与当前输入同句」后仍有备选）。</summary>
    private const int ContextProbeRows = 5;

    private readonly HistoryDatabase _database;

    public HistoryRepository(HistoryDatabase database) => _database = database;

    private sealed class Row
    {
        public long Id { get; init; }
        public long CreatedAtMs { get; init; }
        public string SourceText { get; init; } = "";
        public string TranslatedText { get; init; } = "";
        public string SourceLanguage { get; init; } = "";
        public string TargetLanguage { get; init; } = "";
        public string Engine { get; init; } = "";
        public bool Reviewed { get; init; }
        public bool Rejected { get; init; }
        public long? EditedAtMs { get; init; }
        public bool Pinned { get; init; }

        public TranslationRecord ToRecord() => new(
            Id,
            DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs),
            SourceText, TranslatedText, SourceLanguage, TargetLanguage, Engine,
            Reviewed, Rejected,
            EditedAtMs is { } edited ? DateTimeOffset.FromUnixTimeMilliseconds(edited) : null,
            Pinned);
    }

    public void Add(string sourceText, string translatedText, string sourceLanguage, string targetLanguage, string engine)
    {
        if (!_database.IsAvailable)
        {
            return;
        }

        try
        {
            using var connection = _database.OpenConnection();
            connection.Execute(
                """
                INSERT INTO History (CreatedAtMs, SourceText, TranslatedText, SourceLanguage, TargetLanguage, Engine)
                VALUES (@CreatedAtMs, @SourceText, @TranslatedText, @SourceLanguage, @TargetLanguage, @Engine);
                """,
                new
                {
                    CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SourceText = sourceText,
                    TranslatedText = translatedText,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    Engine = engine,
                });

            connection.Execute(
                """
                DELETE FROM History
                WHERE Pinned = 0 AND Id NOT IN (
                    SELECT Id FROM History ORDER BY Id DESC LIMIT @Max
                );
                """,
                new { Max = MaxRecords });
        }
        catch
        {
            // 历史写入失败不影响翻译结果展示
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        if (!_database.IsAvailable) return;
        try
        {
            using var connection = _database.OpenConnection();
            connection.Execute("UPDATE History SET Pinned = @Pinned WHERE Id = @Id;",
                new { Id = id, Pinned = pinned });
        }
        catch
        {
            // 固定失败不影响历史主流程
        }
    }

    public IReadOnlyList<TranslationRecord> Search(HistoryQuery query)
    {
        if (!_database.IsAvailable) return [];

        try
        {
            using var connection = _database.OpenConnection();
            var keyword = query.Keyword.Trim();
            var conditions = new List<string>();
            var parameters = new Dictionary<string, object> { ["@Limit"] = query.Limit };

            if (keyword.Length > 0)
            {
                conditions.Add("(SourceText LIKE @Pattern ESCAPE '\\' OR TranslatedText LIKE @Pattern ESCAPE '\\')");
                parameters["@Pattern"] = $"%{Escape(keyword)}%";
            }
            if (query.PinnedOnly)
            {
                conditions.Add("Pinned = 1");
            }
            if (!string.IsNullOrWhiteSpace(query.Engine))
            {
                conditions.Add("Engine = @Engine");
                parameters["@Engine"] = query.Engine;
            }
            if (!string.IsNullOrWhiteSpace(query.TargetLanguage))
            {
                conditions.Add("TargetLanguage = @TargetLanguage");
                parameters["@TargetLanguage"] = query.TargetLanguage;
            }
            if (query.From is { } from)
            {
                conditions.Add("CreatedAtMs >= @FromMs");
                parameters["@FromMs"] = from.ToUnixTimeMilliseconds();
            }
            if (query.To is { } to)
            {
                conditions.Add("CreatedAtMs <= @ToMs");
                parameters["@ToMs"] = to.ToUnixTimeMilliseconds();
            }

            var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            var rows = connection.Query<Row>(
                $"""SELECT * FROM History {where} ORDER BY Pinned DESC, Id DESC LIMIT @Limit;""",
                parameters);
            return rows.Select(r => r.ToRecord()).ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>转义 LIKE 通配符，避免用户输入的 % 和 _ 被当作通配符。</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public IReadOnlyList<TmCandidate> TmCandidates(string targetLanguage, int limit = 200)
    {
        if (!_database.IsAvailable)
        {
            return [];
        }

        try
        {
            using var connection = _database.OpenConnection();
            var rows = connection.Query<Row>(
                """
                SELECT SourceText, TranslatedText, CreatedAtMs, Reviewed FROM History
                WHERE TargetLanguage = @TargetLanguage AND Rejected = 0
                ORDER BY Reviewed DESC, Id DESC LIMIT @Limit;
                """,
                new { TargetLanguage = targetLanguage, Limit = limit });
            return rows
                .Select(r => new TmCandidate(r.SourceText, r.TranslatedText,
                    DateTimeOffset.FromUnixTimeMilliseconds(r.CreatedAtMs), r.Reviewed))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// AI 语境（FR-050 / 批 5 spec §1.3）：取同目标语言、<paramref name="maxAgeMinutes"/> 分钟内、
    /// 且与当前输入不同的一条原文。取最近 5 条再在内存里跳过同文本——「刚把同一句译了两遍」
    /// 时上一条恰好就是本句，拿它当语境毫无意义。
    /// </summary>
    public string? ContextSource(string targetLanguage, string currentSource, int maxAgeMinutes = 30)
    {
        if (!_database.IsAvailable || maxAgeMinutes <= 0)
        {
            return null;
        }

        try
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - maxAgeMinutes * 60_000L;
            using var connection = _database.OpenConnection();
            var rows = connection.Query<Row>(
                """
                SELECT SourceText, CreatedAtMs FROM History
                WHERE TargetLanguage = @TargetLanguage AND Rejected = 0 AND CreatedAtMs >= @Cutoff
                ORDER BY Id DESC LIMIT @Limit;
                """,
                new { TargetLanguage = targetLanguage, Cutoff = cutoff, Limit = ContextProbeRows });

            var current = currentSource.Trim();
            foreach (var row in rows)
            {
                var candidate = row.SourceText?.Trim() ?? "";
                if (candidate.Length > 0 && !string.Equals(candidate, current, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Delete(long id)    {
        if (!_database.IsAvailable)
        {
            return;
        }

        try
        {
            using var connection = _database.OpenConnection();
            connection.Execute("DELETE FROM History WHERE Id = @Id;", new { Id = id });
        }
        catch
        {
            // 忽略
        }
    }

    public int Update(long id, string translatedText, bool reviewed, bool rejected)
    {
        if (!_database.IsAvailable) return 0;
        try
        {
            using var connection = _database.OpenConnection();
            return connection.Execute(
                """
                UPDATE History
                SET TranslatedText = @TranslatedText,
                    Reviewed = @Reviewed,
                    Rejected = @Rejected,
                    EditedAtMs = @EditedAtMs
                WHERE Id = @Id;
                """,
                new
                {
                    Id = id,
                    TranslatedText = translatedText,
                    Reviewed = reviewed,
                    Rejected = rejected,
                    EditedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });
        }
        catch
        {
            return 0;
        }
    }

    public void Clear()
    {
        if (!_database.IsAvailable)
        {
            return;
        }

        try
        {
            using var connection = _database.OpenConnection();
            connection.Execute("DELETE FROM History;");
        }
        catch
        {
            // 忽略
        }
    }

    public int Count()
    {
        if (!_database.IsAvailable)
        {
            return 0;
        }

        try
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM History;");
        }
        catch
        {
            return 0;
        }
    }
}
