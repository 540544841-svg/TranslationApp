using TranslationApp.Core.History;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>引擎统计仓储测试（P0 批 1 / spec §4）。</summary>
public class EngineStatsRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sv-stats-test-{Guid.NewGuid():N}.db");
    private readonly HistoryDatabase _db;
    private readonly EngineStatsRepository _repo;

    public EngineStatsRepositoryTests()
    {
        _db = new HistoryDatabase(_dbPath);
        _db.Initialize();
        Assert.True(_db.IsAvailable);
        _repo = new EngineStatsRepository(_db);
    }

    private void InsertRow(string engine, string day, int success = 0, int failNetwork = 0, string lastError = "")
    {
        using var c = _db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO EngineStats(EngineId,Day,Success,FailNetwork,LastError) VALUES($e,$d,$s,$f,$err)";
        cmd.Parameters.AddWithValue("$e", engine);
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$s", success);
        cmd.Parameters.AddWithValue("$f", failNetwork);
        cmd.Parameters.AddWithValue("$err", lastError);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Record_Success_IncrementsAndSummarized()
    {
        _repo.Record("bing", EngineOutcome.Success);
        _repo.Record("bing", EngineOutcome.Success);

        var s = _repo.GetSummary("bing");

        Assert.Equal(2, s.Success);
        Assert.Equal(0, s.FailTotal);
    }

    [Fact]
    public void Record_FailOutcomes_GoToTheirOwnColumns()
    {
        _repo.Record("google", EngineOutcome.FailNetwork);
        _repo.Record("google", EngineOutcome.FailQuota);
        _repo.Record("google", EngineOutcome.FailKey);
        _repo.Record("google", EngineOutcome.FailEngine);

        var s = _repo.GetSummary("google");

        Assert.Equal(1, s.FailNetwork);
        Assert.Equal(1, s.FailQuota);
        Assert.Equal(1, s.FailKey);
        Assert.Equal(1, s.FailEngine);
        Assert.Equal(4, s.FailTotal);
    }

    [Fact]
    public void Record_FallbackUsed_CountsSeparatelyFromFail()
    {
        _repo.Record("google", EngineOutcome.FailNetwork);
        _repo.Record("google", EngineOutcome.FallbackUsed);

        var s = _repo.GetSummary("google");

        Assert.Equal(1, s.FailNetwork);
        Assert.Equal(1, s.FallbackUsed);
    }

    [Fact]
    public void LastError_UpdatedOnFail_KeptOnSuccess()
    {
        _repo.Record("bing", EngineOutcome.FailQuota, "HTTP 429");
        Assert.Equal("HTTP 429", _repo.GetSummary("bing").LastError);

        _repo.Record("bing", EngineOutcome.Success);
        Assert.Equal("HTTP 429", _repo.GetSummary("bing").LastError);

        _repo.Record("bing", EngineOutcome.FailNetwork, "HTTP 502");
        Assert.Equal("HTTP 502", _repo.GetSummary("bing").LastError);
    }

    [Fact]
    public void GetSummary_ExcludesDaysOutsideWindow()
    {
        InsertRow("bing", DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd"), success: 99);
        _repo.Record("bing", EngineOutcome.Success);

        var s = _repo.GetSummary("bing", days: 7);

        Assert.Equal(1, s.Success);
    }

    [Fact]
    public void Prune_RemovesRowsOlderThanCutoff()
    {
        InsertRow("bing", DateTime.Now.AddDays(-40).ToString("yyyy-MM-dd"), success: 5);
        InsertRow("bing", DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"), success: 3);

        _repo.PruneOlderThan(30);

        Assert.Equal(3, _repo.GetSummary("bing", days: 365).Success);
    }

    [Fact]
    public void UnknownEngine_ReturnsZeroSummary()
    {
        var s = _repo.GetSummary("nope");

        Assert.Equal(0, s.Success);
        Assert.Equal(0, s.FailTotal);
        Assert.Equal("", s.LastError);
    }

    [Fact]
    public void UnavailableDatabase_IsSafeNoOp()
    {
        var dead = new HistoryDatabase(Path.Combine(_dbPath, "sub", "x.db")); // 不 Initialize，IsAvailable=false
        var repo = new EngineStatsRepository(dead);

        repo.Record("bing", EngineOutcome.Success); // 不抛
        Assert.Equal(0, repo.GetSummary("bing").Success);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 临时文件残留可接受 */ }
    }
}
