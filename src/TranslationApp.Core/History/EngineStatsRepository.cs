using Microsoft.Data.Sqlite;

namespace TranslationApp.Core.History;

/// <summary>一次引擎调用的结果类型（P0 批 1 / spec §4）。FallbackUsed 与失败并列计，不重复计失败。</summary>
public enum EngineOutcome
{
    Success,
    FailNetwork,
    FailEngine,
    FailKey,
    FailQuota,
    FallbackUsed,
}

/// <summary>窗口聚合结果，供设置页引擎卡看板行直接渲染。</summary>
public sealed record EngineStatsSummary(
    int Success,
    int FailNetwork,
    int FailEngine,
    int FailKey,
    int FailQuota,
    int FallbackUsed,
    string LastError)
{
    public int FailTotal => FailNetwork + FailEngine + FailKey + FailQuota;
    public bool IsEmpty => Success == 0 && FailTotal == 0 && FallbackUsed == 0;
}

/// <summary>
/// 引擎成败统计（P0 批 1 / spec §4.1）：按「引擎 × 本地日」UPSERT 累加，只存计数与错误摘要，
/// **绝不存用户文本**。数据库不可用时全部安全空操作（与历史仓储同一降级纪律）。
/// 隐私模式的门控在调用方（App 层不进来就什么都不会发生）。
/// </summary>
public sealed class EngineStatsRepository
{
    private readonly HistoryDatabase _db;

    public EngineStatsRepository(HistoryDatabase db) => _db = db;

    public void Record(string engineId, EngineOutcome outcome, string? errorSummary = null)
    {
        if (!_db.IsAvailable) return;

        var column = outcome switch
        {
            EngineOutcome.Success => "Success",
            EngineOutcome.FailNetwork => "FailNetwork",
            EngineOutcome.FailEngine => "FailEngine",
            EngineOutcome.FailKey => "FailKey",
            EngineOutcome.FailQuota => "FailQuota",
            EngineOutcome.FallbackUsed => "FallbackUsed",
            _ => null,
        };
        if (column is null) return;

        try
        {
            using var connection = _db.OpenConnection();
            using var command = connection.CreateCommand();
            // 列名来自上方白名单，非用户输入，无注入面
            command.CommandText = $"""
                INSERT INTO EngineStats(EngineId, Day, {column}, LastError)
                VALUES($e, $d, 1, $err)
                ON CONFLICT(EngineId, Day) DO UPDATE SET
                    {column} = {column} + 1,
                    LastError = CASE WHEN excluded.LastError <> '' THEN excluded.LastError ELSE LastError END;
                """;
            command.Parameters.AddWithValue("$e", engineId);
            command.Parameters.AddWithValue("$d", DateTime.Now.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$err", outcome == EngineOutcome.Success ? "" : (errorSummary ?? ""));
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 统计失败绝不影响翻译主流程
        }
    }

    public EngineStatsSummary GetSummary(string engineId, int days = 7)
    {
        if (!_db.IsAvailable) return new EngineStatsSummary(0, 0, 0, 0, 0, 0, "");

        var cutoff = DateTime.Now.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
        try
        {
            using var connection = _db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT SUM(Success), SUM(FailNetwork), SUM(FailEngine), SUM(FailKey), SUM(FailQuota), SUM(FallbackUsed)
                FROM EngineStats WHERE EngineId = $e AND Day >= $cutoff;

                SELECT LastError FROM EngineStats
                WHERE EngineId = $e AND Day >= $cutoff AND LastError <> ''
                ORDER BY Day DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$e", engineId);
            command.Parameters.AddWithValue("$cutoff", cutoff);

            using var reader = command.ExecuteReader();
            int s = 0, fn = 0, fe = 0, fk = 0, fq = 0, fb = 0;
            if (reader.Read())
            {
                s = ToInt(reader, 0); fn = ToInt(reader, 1); fe = ToInt(reader, 2);
                fk = ToInt(reader, 3); fq = ToInt(reader, 4); fb = ToInt(reader, 5);
            }
            string lastError = "";
            if (reader.NextResult() && reader.Read()) lastError = reader.GetString(0);

            return new EngineStatsSummary(s, fn, fe, fk, fq, fb, lastError);
        }
        catch (SqliteException)
        {
            return new EngineStatsSummary(0, 0, 0, 0, 0, 0, "");
        }

        static int ToInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : (int)r.GetInt64(i);
    }

    /// <summary>删除超过保留期的旧行（看板只看 7 天，30 天是数据洁癖上限）。</summary>
    public void PruneOlderThan(int days = 30)
    {
        if (!_db.IsAvailable) return;
        var cutoff = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd");
        try
        {
            using var connection = _db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM EngineStats WHERE Day < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 清理失败无所谓，下次再来
        }
    }
}
