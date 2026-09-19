using System.Text;
using Dapper;

namespace TranslationApp.Core.History;

/// <summary>生词本仓储（FR-015）。独立于历史记录，不受历史 5000 条清理影响。</summary>
public interface IVocabularyRepository
{
    /// <summary>
    /// 收藏一条生词。同一「原文 + 目标语言」只保留一条：
    /// 已存在时更新译文与时间并返回 false（表示是更新而非新增）。
    /// </summary>
    bool Add(string sourceText, string translatedText, string sourceLanguage, string targetLanguage);

    /// <summary>全部生词，按时间倒序。</summary>
    IReadOnlyList<VocabularyEntry> List();

    /// <summary>判断是否已收藏（小窗按钮状态用）。</summary>
    bool Contains(string sourceText, string targetLanguage);

    void Delete(long id);

    int Count();

    /// <summary>导出为 CSV（RFC 4180 转义，带 UTF-8 BOM 便于 Excel 直接打开）。</summary>
    string ExportCsv();

    /// <summary>导出为 Anki 可导入的 TSV（正面 / 背面 / 标签三列）。</summary>
    string ExportTsv();
}

/// <summary>SQLite 实现。数据库不可用时安全返回空结果。</summary>
public sealed class VocabularyRepository : IVocabularyRepository
{
    private readonly HistoryDatabase _database;

    public VocabularyRepository(HistoryDatabase database) => _database = database;

    private sealed class Row
    {
        public long Id { get; init; }
        public long CreatedAtMs { get; init; }
        public string SourceText { get; init; } = "";
        public string TranslatedText { get; init; } = "";
        public string SourceLanguage { get; init; } = "";
        public string TargetLanguage { get; init; } = "";

        public VocabularyEntry ToEntry() => new(
            Id,
            DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs),
            SourceText, TranslatedText, SourceLanguage, TargetLanguage);
    }

    public bool Add(string sourceText, string translatedText, string sourceLanguage, string targetLanguage)
    {
        if (!_database.IsAvailable || string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        try
        {
            using var connection = _database.OpenConnection();
            var existingId = connection.ExecuteScalar<long?>(
                "SELECT Id FROM Vocabulary WHERE SourceText = @SourceText AND TargetLanguage = @TargetLanguage;",
                new { SourceText = sourceText, TargetLanguage = targetLanguage });

            if (existingId is not null)
            {
                // 已收藏：刷新译文与时间（译文可能因换引擎而不同）
                connection.Execute(
                    """
                    UPDATE Vocabulary
                    SET TranslatedText = @TranslatedText, SourceLanguage = @SourceLanguage,
                        TargetLanguage = @TargetLanguage, CreatedAtMs = @CreatedAtMs
                    WHERE Id = @Id;
                    """,
                    new
                    {
                        Id = existingId.Value,
                        TranslatedText = translatedText,
                        SourceLanguage = sourceLanguage,
                        TargetLanguage = targetLanguage,
                        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                return false;
            }

            connection.Execute(
                """
                INSERT INTO Vocabulary (CreatedAtMs, SourceText, TranslatedText, SourceLanguage, TargetLanguage)
                VALUES (@CreatedAtMs, @SourceText, @TranslatedText, @SourceLanguage, @TargetLanguage);
                """,
                new
                {
                    CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SourceText = sourceText,
                    TranslatedText = translatedText,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<VocabularyEntry> List()
    {
        if (!_database.IsAvailable)
        {
            return [];
        }

        try
        {
            using var connection = _database.OpenConnection();
            var rows = connection.Query<Row>("SELECT * FROM Vocabulary ORDER BY Id DESC;");
            return rows.Select(r => r.ToEntry()).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public bool Contains(string sourceText, string targetLanguage)
    {
        if (!_database.IsAvailable || string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        try
        {
            using var connection = _database.OpenConnection();
            return connection.ExecuteScalar<long?>(
                "SELECT Id FROM Vocabulary WHERE SourceText = @SourceText AND TargetLanguage = @TargetLanguage;",
                new { SourceText = sourceText, TargetLanguage = targetLanguage }) is not null;
        }
        catch
        {
            return false;
        }
    }

    public void Delete(long id)
    {
        if (!_database.IsAvailable)
        {
            return;
        }

        try
        {
            using var connection = _database.OpenConnection();
            connection.Execute("DELETE FROM Vocabulary WHERE Id = @Id;", new { Id = id });
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
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM Vocabulary;");
        }
        catch
        {
            return 0;
        }
    }

    public string ExportCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("原文,译文,源语言,目标语言,收藏时间");
        foreach (var entry in List())
        {
            builder.AppendLine(string.Join(',',
                Csv(entry.SourceText),
                Csv(entry.TranslatedText),
                Csv(entry.SourceLanguage),
                Csv(entry.TargetLanguage),
                Csv(entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))));
        }

        return builder.ToString();
    }

    public string ExportTsv()
    {
        var builder = new StringBuilder();
        // Anki 导入用三列：正面 / 背面 / 标签
        foreach (var entry in List())
        {
            builder.AppendLine(string.Join('\t',
                Tsv(entry.SourceText),
                Tsv(entry.TranslatedText),
                Tsv($"{entry.SourceLanguage}-{entry.TargetLanguage}")));
        }

        return builder.ToString();
    }

    /// <summary>CSV 字段转义（RFC 4180：含逗号/引号/换行时用双引号包裹，内部引号翻倍）。</summary>
    internal static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>TSV 字段转义（制表符与换行替换为空格，避免破坏列结构）。</summary>
    internal static string Tsv(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
