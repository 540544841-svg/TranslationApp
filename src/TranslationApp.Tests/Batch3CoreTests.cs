using TranslationApp.Core.History;
using TranslationApp.Core.Settings;
using TranslationApp.Core.SystemIntegration;
using TranslationApp.Core.Translation;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-038：双击修饰键检测（纯状态机，时钟注入）。spec §1。</summary>
public class ModifierKeyDoubleTapDetectorTests
{
    public const int Alt = 0x12;
    public const int Tab = 0x09;

    private long _now;
    private readonly List<int> _fired = [];

    private ModifierKeyDoubleTapDetector New(int targetVk = Alt) =>
        new(() => _now, targetVk, () => _fired.Add(1));

    [Fact]
    public void TwoQuickTaps_Fires()
    {
        var d = New();
        d.OnKeyDown(Alt); _now = 80;
        d.OnKeyUp(Alt);
        _now = 200; // 抬起后 120ms 再按
        d.OnKeyDown(Alt); _now = 260;
        d.OnKeyUp(Alt);
        Assert.Single(_fired);
    }

    [Fact]
    public void SecondTapOutsideWindow_DoesNotFire_ButStartsNewFirstTap()
    {
        var d = New();
        d.OnKeyDown(Alt); _now = 60;
        d.OnKeyUp(Alt);
        _now = 60 + ModifierKeyDoubleTapDetector.DefaultWindowMs + 1;
        d.OnKeyDown(Alt); // 超窗：这次不算双击，但应成为新的第一次
        _now += 100;
        d.OnKeyUp(Alt);
        _now += 100;
        d.OnKeyDown(Alt); _now += 40; d.OnKeyUp(Alt);
        Assert.Single(_fired);
    }

    [Fact]
    public void HoldWithOtherKey_AltTab_Invalidates()
    {
        var d = New();
        d.OnKeyDown(Alt); _now = 50;
        d.OnKeyDown(Tab); // 按住 Alt 时混入其他键
        d.OnKeyUp(Tab);
        _now = 90;
        d.OnKeyUp(Alt);
        _now = 150;
        d.OnKeyDown(Alt); _now = 200; d.OnKeyUp(Alt);
        Assert.Empty(_fired);
    }

    [Fact]
    public void OtherKeyAfterRelease_DoesNotInvalidate()
    {
        // 第一次双击前半段完整结束后按了别的键：不影响第二次独立按下-抬起构成双击
        var d = New();
        d.OnKeyDown(Alt); _now = 50; d.OnKeyUp(Alt);
        _now = 80; d.OnKeyDown(Tab); d.OnKeyUp(Tab);
        _now = 150; d.OnKeyDown(Alt); _now = 190; d.OnKeyUp(Alt);
        Assert.Single(_fired);
    }

    [Fact]
    public void Cooldown_AfterFire_SuppressesImmediateRefire()
    {
        var d = New();
        d.OnKeyDown(Alt); _now = 50; d.OnKeyUp(Alt);
        _now = 120; d.OnKeyDown(Alt); _now = 160; d.OnKeyUp(Alt);
        Assert.Single(_fired);

        // 触发后立刻再来一轮快速双击：冷却 500ms 内不再触发
        _now = 200; d.OnKeyDown(Alt); _now = 240; d.OnKeyUp(Alt);
        _now = 300; d.OnKeyDown(Alt); _now = 340; d.OnKeyUp(Alt);
        Assert.Single(_fired);

        _now = 800; d.OnKeyDown(Alt); _now = 840; d.OnKeyUp(Alt);
        _now = 900; d.OnKeyDown(Alt); _now = 940; d.OnKeyUp(Alt);
        Assert.Equal(2, _fired.Count);
    }

    [Fact]
    public void OtherKeyCodes_AreIgnoredEntirely()
    {
        var d = New();
        d.OnKeyDown(Tab); d.OnKeyUp(Tab);
        d.OnKeyDown(Tab); d.OnKeyUp(Tab);
        Assert.Empty(_fired);
    }

    [Fact]
    public void SelfForegroundAtSecondPress_DoesNotFire()
    {
        var d = New();
        d.OnKeyDown(Alt, foregroundIsSelf: false); _now = 50; d.OnKeyUp(Alt);
        _now = 120; d.OnKeyDown(Alt, foregroundIsSelf: true); _now = 160; d.OnKeyUp(Alt);
        Assert.Empty(_fired);
    }

    [Fact]
    public void TargetKeyIsConfigurable_Ctrl()
    {
        var d = New(targetVk: 0x11); // Ctrl
        d.OnKeyDown(0x11); _now = 50; d.OnKeyUp(0x11);
        _now = 120; d.OnKeyDown(0x11); _now = 160; d.OnKeyUp(0x11);
        Assert.Single(_fired);

        // Alt 双击不再触发
        _now = 1000;
        d.OnKeyDown(Alt); _now += 50; d.OnKeyUp(Alt);
        _now += 50; d.OnKeyDown(Alt); _now += 50; d.OnKeyUp(Alt);
        Assert.Single(_fired);
    }
}

/// <summary>FR-041：术语反向保护（Target 已在原文 → 跳过并计冲突）。spec §4。</summary>
public class GlossaryReverseProtectionTests
{
    private static GlossaryItem Item(string s, string t) => new(s, t);

    [Fact]
    public void TargetPresentInSource_SkipsItemAndRecordsConflict()
    {
        var g = GlossaryReplacer.Apply(
            "This model is good",
            [Item("model", "模型")],
            sourceText: "这个模型很好"); // 原文已含 Target

        Assert.Equal("This model is good", g.Text); // 不替换
        Assert.Equal(0, g.Hits);
        var conflict = Assert.Single(g.Conflicts);
        Assert.Equal("model", conflict.Source);
        Assert.Equal("模型", conflict.Target);
    }

    [Fact]
    public void TargetAbsentInSource_ReplacesNormally()
    {
        var g = GlossaryReplacer.Apply(
            "This model is good",
            [Item("model", "型号")],
            sourceText: "这个机器很好");

        Assert.Equal("This 型号 is good", g.Text);
        Assert.Equal(1, g.Hits);
        Assert.Empty(g.Conflicts);
    }

    [Fact]
    public void NullSourceText_BehavesLikeBatchOne()
    {
        var g = GlossaryReplacer.Apply("This model is good", [Item("model", "模型")]);
        Assert.Equal("This 模型 is good", g.Text);
        Assert.Empty(g.Conflicts);
    }

    [Fact]
    public void ConflictCheckIgnoresCase()
    {
        var g = GlossaryReplacer.Apply(
            "the ai agent ran",
            [Item("agent", "AI")],
            sourceText: "An Ai 与一个 agent");
        Assert.Equal("the ai agent ran", g.Text);
        Assert.Single(g.Conflicts);
    }

    [Fact]
    public void MixedItems_OnlyConflictingSkipped()
    {
        var g = GlossaryReplacer.Apply(
            "ship and plane",
            [Item("ship", "船舶"), Item("plane", "飞机")],
            sourceText: "船舶与一架 plane");

        // ship 冲突跳过 → 译文保留原词；plane 正常替换
        Assert.Equal("ship and 飞机", g.Text);
        Assert.Equal(1, g.Hits);
        var conflict = Assert.Single(g.Conflicts);
        Assert.Equal("ship", conflict.Source);
    }
}

/// <summary>FR-042：看板 P50 延迟（样本存储 / 中位数 / 旧库迁移）。spec §5。</summary>
public class EngineStatsLatencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sv-latency-test-{Guid.NewGuid():N}.db");
    private readonly HistoryDatabase _db;
    private readonly EngineStatsRepository _repo;

    public EngineStatsLatencyTests()
    {
        _db = new HistoryDatabase(_dbPath);
        _db.Initialize();
        Assert.True(_db.IsAvailable);
        _repo = new EngineStatsRepository(_db);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 连接池未放行时临时文件残留可接受（与批 1 同款） */ }
    }

    [Fact]
    public void P50_OddSampleCount_IsMiddleValue()
    {
        foreach (var ms in new long[] { 300, 100, 200 })
        {
            _repo.Record("bing", EngineOutcome.Success, latencyMs: ms);
        }
        Assert.Equal(200, _repo.GetSummary("bing").P50Ms);
    }

    [Fact]
    public void P50_EvenSampleCount_IsUpperMedian()
    {
        foreach (var ms in new long[] { 400, 100, 300, 200 })
        {
            _repo.Record("bing", EngineOutcome.Success, latencyMs: ms);
        }
        Assert.Equal(300, _repo.GetSummary("bing").P50Ms);
    }

    [Fact]
    public void NoSamples_P50IsNull()
    {
        _repo.Record("bing", EngineOutcome.FailNetwork, "HTTP 500");
        Assert.Null(_repo.GetSummary("bing").P50Ms);
    }

    [Fact]
    public void FailureRecords_DoNotAddLatencySamples()
    {
        _repo.Record("bing", EngineOutcome.Success, latencyMs: 100);
        _repo.Record("bing", EngineOutcome.FailQuota, "429");
        Assert.Equal(100, _repo.GetSummary("bing").P50Ms); // 失败不掺入样本
    }

    [Fact]
    public void SampleCap_KeepsMostRecent200()
    {
        for (long i = 1; i <= 250; i++)
        {
            _repo.Record("bing", EngineOutcome.Success, latencyMs: i);
        }
        // 保留最近 200 条（51..250）→ 升序后取索引 100 = 151
        Assert.Equal(151, _repo.GetSummary("bing").P50Ms);
    }

    [Fact]
    public void LegacyDatabaseWithoutLatenciesColumn_MigratesOnInitialize()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"sv-latency-legacy-{Guid.NewGuid():N}.db");
        try
        {
            // 手工建一张没有 Latencies 列的旧表（模拟批 1 库）
            var legacy = new HistoryDatabase(legacyPath);
            legacy.Initialize();
            using (var c = legacy.OpenConnection())
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "ALTER TABLE EngineStats DROP COLUMN Latencies;";
                cmd.ExecuteNonQuery();
            }

            var reopened = new HistoryDatabase(legacyPath);
            reopened.Initialize();
            Assert.True(reopened.IsAvailable);
            var repo = new EngineStatsRepository(reopened);
            repo.Record("bing", EngineOutcome.Success, latencyMs: 42);
            Assert.Equal(42, repo.GetSummary("bing").P50Ms);
        }
        finally
        {
            try { File.Delete(legacyPath); } catch { /* 临时文件残留可接受 */ }
        }
    }
}

/// <summary>批 3 新设置字段：默认值与旧配置兼容（spec §6）。</summary>
public class Batch3SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "TranslationAppTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Defaults_Batch3Fields()
    {
        var s = new AppSettings();
        Assert.False(s.DoubleTapTranslateEnabled);
        Assert.Equal("alt", s.DoubleTapKey);
        Assert.False(s.MouseSideButtonSelect);
        Assert.False(s.MouseSideButtonCapture);
        Assert.True(s.PasteTranslateEnabled);
    }

    [Fact]
    public void LegacyJson_LoadsWithDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"Engine":"deepl","HoverSelectEnabled":true}""");
        var s = new JsonSettingsStore(FilePath).Load();
        Assert.True(s.HoverSelectEnabled);
        Assert.False(s.DoubleTapTranslateEnabled);
        Assert.True(s.PasteTranslateEnabled);
    }

    [Fact]
    public void RoundTrip_Batch3Fields()
    {
        var store = new JsonSettingsStore(FilePath);
        store.Save(new AppSettings
        {
            DoubleTapTranslateEnabled = true,
            DoubleTapKey = "ctrl",
            MouseSideButtonSelect = true,
            PasteTranslateEnabled = false,
        });
        var s = store.Load();
        Assert.True(s.DoubleTapTranslateEnabled);
        Assert.Equal("ctrl", s.DoubleTapKey);
        Assert.True(s.MouseSideButtonSelect);
        Assert.False(s.PasteTranslateEnabled);
    }
}
