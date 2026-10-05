using System.IO.Compression;
using System.Text;
using TranslationApp.Core.Backup;
using TranslationApp.Core.History;
using Xunit;

namespace TranslationApp.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDirectory;
    private readonly HistoryDatabase _database;
    private readonly BackupService _backup;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ta-backup-{Guid.NewGuid():N}");
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _database = new HistoryDatabase(Path.Combine(_dataDirectory, "history.db"));
        _database.Initialize();
        _backup = new BackupService(_dataDirectory, _database);
    }

    [Fact]
    public void BackupAndRestore_RestoresSettingsAndConsistentDatabase()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"Version\":\"old\"}");
        var history = new HistoryRepository(_database);
        history.Add("old source", "old translation", "en", "zh-CN", "Bing");
        var archive = Path.Combine(_root, "backup.zip");

        var manifest = _backup.BackupTo(archive, "1.0.0");
        Assert.Equal(2, manifest.Files.Count);

        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"Version\":\"new\"}");
        history.Add("new source", "new translation", "en", "zh-CN", "Bing");

        var restored = _backup.RestoreFrom(archive);

        Assert.True(restored.IsValid, restored.Message);
        Assert.Contains("\"old\"", File.ReadAllText(Path.Combine(_dataDirectory, "settings.json")), StringComparison.Ordinal);
        // 必须在任何查询之前检查：查询会重新打开 SQLite 并合法地创建新的 -wal/-shm。
        Assert.False(File.Exists(_database.DatabasePath + "-wal"));
        Assert.False(File.Exists(_database.DatabasePath + "-shm"));
        Assert.Single(history.Search(new HistoryQuery()));
        Assert.Equal("old source", history.Search(new HistoryQuery())[0].SourceText);
    }

    /// <summary>
    /// 回归：恢复必须先把连接池里握着的旧连接收干、再删 -wal/-shm，**最后**才覆盖主文件。
    /// 顺序颠倒时，关闭旧连接触发的那次 WAL 检查点会写进刚恢复的主文件，
    /// 把备份之后新写入的行带回来（干净 CI runner 上稳定复现；本机因检查点时机不同常常看不出来）。
    /// </summary>
    [Fact]
    public void Restore_先清理旧WAL再覆盖_不把备份之后的行带回来()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"old\":true}");
        var history = new HistoryRepository(_database);
        history.Add("old source", "old translation", "en", "zh-CN", "Bing");
        var archive = Path.Combine(_root, "backup.zip");
        _backup.BackupTo(archive, "1.0.0");
        history.Add("new source", "new translation", "en", "zh-CN", "Bing");

        // 让池里确实握着一个连接：ClearAllPools 会物理关闭它并触发 WAL 检查点，
        // 这正是缺陷发生时把旧行写回新文件的时机。
        using (var pooled = _database.OpenConnection())
        {
            using var count = pooled.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM History;";
            count.ExecuteScalar();
        }

        var restored = _backup.RestoreFrom(archive);

        Assert.True(restored.IsValid, restored.Message);
        Assert.False(File.Exists(_database.DatabasePath + "-wal"));
        Assert.False(File.Exists(_database.DatabasePath + "-shm"));
        var rows = history.Search(new HistoryQuery());
        Assert.Single(rows);
        Assert.Equal("old source", rows[0].SourceText);
    }

    [Fact]
    public void Inspect_RejectsTamperedFileHash()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"Version\":\"old\"}");
        var archive = Path.Combine(_root, "backup.zip");
        _backup.BackupTo(archive, "1.0.0");
        ReplaceEntry(archive, "settings.json", "{\"Version\":\"tampered\"}");

        var inspection = _backup.Inspect(archive);

        Assert.False(inspection.IsValid);
        Assert.Contains("校验失败", inspection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_RejectsEntryOutsideWhitelist()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"Version\":\"old\"}");
        var archive = Path.Combine(_root, "backup.zip");
        _backup.BackupTo(archive, "1.0.0");
        AddEntry(archive, "extra.txt", "not allowed");

        var inspection = _backup.Inspect(archive);

        Assert.False(inspection.IsValid);
        Assert.Contains("不允许", inspection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_RejectsPathTraversalEntry()
    {
        File.WriteAllText(Path.Combine(_dataDirectory, "settings.json"), "{\"Version\":\"old\"}");
        var archive = Path.Combine(_root, "backup.zip");
        _backup.BackupTo(archive, "1.0.0");
        AddEntry(archive, "../escape.txt", "not allowed");

        var inspection = _backup.Inspect(archive);

        Assert.False(inspection.IsValid);
        Assert.Contains("不允许", inspection.Message, StringComparison.Ordinal);
    }

    private static void ReplaceEntry(string archivePath, string entryName, string content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)?.Delete();
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static void AddEntry(string archivePath, string entryName, string content)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

