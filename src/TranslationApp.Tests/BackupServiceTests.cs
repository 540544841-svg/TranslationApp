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
        Assert.Single(history.Search(null));
        Assert.Equal("old source", history.Search(null)[0].SourceText);
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
