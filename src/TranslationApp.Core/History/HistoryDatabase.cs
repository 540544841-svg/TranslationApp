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
                    ,Reviewed       INTEGER NOT NULL DEFAULT 0
                    ,Rejected       INTEGER NOT NULL DEFAULT 0
                    ,EditedAtMs     INTEGER NULL
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

                CREATE TABLE IF NOT EXISTS EngineStats (
                    EngineId      TEXT    NOT NULL,
                    Day           TEXT    NOT NULL,  -- yyyy-MM-dd 本地日期
                    Success       INTEGER NOT NULL DEFAULT 0,
                    FailNetwork   INTEGER NOT NULL DEFAULT 0,
                    FailEngine    INTEGER NOT NULL DEFAULT 0,
                    FailKey       INTEGER NOT NULL DEFAULT 0,
                    FailQuota     INTEGER NOT NULL DEFAULT 0,
                    FallbackUsed  INTEGER NOT NULL DEFAULT 0,
                    LastError     TEXT    NOT NULL DEFAULT '',
                    Latencies     TEXT    NOT NULL DEFAULT '', -- FR-042：成功耗时样本（ms，逗号分隔，≤200 条）
                    PRIMARY KEY (EngineId, Day)
                );
                """;
            command.ExecuteNonQuery();

            // FR-042：批 1 旧库没有 Latencies 列——检测后补列（SQLite 无版本化迁移框架，按列探测即可）
            using (var check = connection.CreateCommand())
            {
                check.CommandText =
                    "SELECT COUNT(*) FROM pragma_table_info('EngineStats') WHERE name = 'Latencies';";
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using var alter = connection.CreateCommand();
                    alter.CommandText =
                        "ALTER TABLE EngineStats ADD COLUMN Latencies TEXT NOT NULL DEFAULT '';";
                    alter.ExecuteNonQuery();
                }
            }

            // 可编辑 TM：Reviewed = 人工校对，Rejected = 禁止复用；旧库按两个布尔列无损升级。
            EnsureColumn(connection, "History", "Reviewed", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "History", "Rejected", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "History", "EditedAtMs", "INTEGER NULL");
            using (var index = connection.CreateCommand())
            {
                index.CommandText =
                    "CREATE INDEX IF NOT EXISTS IX_History_TmQuality ON History(TargetLanguage, Rejected, Reviewed);";
                index.ExecuteNonQuery();
            }
            IsAvailable = true;
            UnavailableReason = null;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
    }

    /// <summary>用一个一致性 SQLite 快照备份当前数据库；目标文件不存在时创建。</summary>
    public void BackupTo(string destinationPath)
    {
        if (!IsAvailable) throw new InvalidOperationException("历史数据库不可用，无法备份");
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var source = OpenConnection();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @Column;";
        check.Parameters.AddWithValue("@Column", column);
        if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) != 0) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }
}
