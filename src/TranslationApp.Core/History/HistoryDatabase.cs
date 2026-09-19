using Microsoft.Data.Sqlite;

namespace TranslationApp.Core.History;

/// <summary>
/// SQLite 数据库（FR-014/015）的连接与建表。
/// 设计要点：
/// 1) 每次操作开一个短连接（SQLite 推荐用法），WAL 模式提升读写并发健壮性；
/// 2) 时间统一存 Unix 毫秒（INTEGER），避免 ORM 对 DateTimeOffset 的类型转换差异；
/// 3) 初始化失败时 IsAvailable=false，历史/生词本整体降级关闭，绝不影响翻译主流程；
///    Core 层不依赖日志框架，失败原因通过 UnavailableReason 交给调用方记录与提示。
/// </summary>
public sealed class HistoryDatabase
{
    private readonly string _connectionString;

    public HistoryDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>数据库是否可用（初始化成功）。不可用时仓储操作全部为安全空操作。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>初始化失败原因（设置页提示与日志用）。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>打开一个新连接（调用方负责释放）。</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>建表并做基本可用性检查；失败不抛异常（降级为不可用）。</summary>
    public void Initialize()
    {
        try
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS History (
                    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAtMs    INTEGER NOT NULL,
                    SourceText     TEXT    NOT NULL,
                    TranslatedText TEXT    NOT NULL,
                    SourceLanguage TEXT    NOT NULL,
                    TargetLanguage TEXT    NOT NULL,
                    Engine         TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_History_CreatedAt ON History(CreatedAtMs DESC);

                CREATE TABLE IF NOT EXISTS Vocabulary (
                    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    CreatedAtMs    INTEGER NOT NULL,
                    SourceText     TEXT    NOT NULL,
                    TranslatedText TEXT    NOT NULL,
                    SourceLanguage TEXT    NOT NULL,
                    TargetLanguage TEXT    NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Vocabulary_Unique
                    ON Vocabulary(SourceText, TargetLanguage);
                """;
            command.ExecuteNonQuery();

            IsAvailable = true;
            UnavailableReason = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
    }
}
