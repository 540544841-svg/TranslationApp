using Dapper;

namespace TranslationApp.Core.History;

/// <summary>历史记录仓储（FR-014）。</summary>
public interface IHistoryRepository
{
    /// <summary>写入一条翻译记录，并自动清理超出上限的旧记录。</summary>
    void Add(string sourceText, string translatedText, string sourceLanguage, string targetLanguage, string engine);

    /// <summary>按关键字模糊搜索（原文或译文），关键字为空时返回最近的记录；按时间倒序。</summary>
    IReadOnlyList<TranslationRecord> Search(string? keyword, int limit = 500);

    void Delete(long id);

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

        public TranslationRecord ToRecord() => new(
            Id,
            DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs),
            SourceText, TranslatedText, SourceLanguage, TargetLanguage, Engine);
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
                WHERE Id NOT IN (SELECT Id FROM History ORDER BY Id DESC LIMIT @Max);
                """,
                new { Max = MaxRecords });
        }
        catch
        {
            // 历史写入失败不影响翻译结果展示
        }
    }

    public IReadOnlyList<TranslationRecord> Search(string? keyword, int limit = 500)
    {
        if (!_database.IsAvailable)
        {
            return [];
        }

        try
        {
            using var connection = _database.OpenConnection();
            var trimmed = keyword?.Trim();

            var rows = string.IsNullOrEmpty(trimmed)
                ? connection.Query<Row>(
                    "SELECT * FROM History ORDER BY Id DESC LIMIT @Limit;",
                    new { Limit = limit })
                : connection.Query<Row>(
                    """
                    SELECT * FROM History
                    WHERE SourceText LIKE @Pattern ESCAPE '\' OR TranslatedText LIKE @Pattern ESCAPE '\'
                    ORDER BY Id DESC LIMIT @Limit;
                    """,
                    new { Pattern = $"%{Escape(trimmed)}%", Limit = limit });

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

    public void Delete(long id)
    {
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
