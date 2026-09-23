using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TranslationApp.Core.History;

namespace TranslationApp.Core.Backup;

public sealed record BackupFileEntry(string Path, long SizeBytes, string Sha256);

public sealed record BackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    string AppVersion,
    IReadOnlyList<BackupFileEntry> Files);

public sealed record BackupInspection(
    bool IsValid,
    string Message,
    BackupManifest? Manifest = null);

/// <summary>
/// 本地数据备份。只打包白名单文件（设置与一致性 SQLite 快照），不收集日志、词典大文件或任何网络内容。
/// ZIP 内每个文件都带 SHA-256；恢复先全部解压校验，再原子替换，失败时回滚原文件。
/// </summary>
public sealed class BackupService
{
    public const int CurrentFormatVersion = 1;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxTotalEntryBytes = 1024L * 1024 * 1024;
    private const long MaxManifestBytes = 1024L * 1024;
    private const string ManifestEntry = "backup-manifest.json";
    private const string SettingsEntry = "settings.json";
    private const string HistoryEntry = "history.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dataDirectory;
    private readonly HistoryDatabase? _database;

    public BackupService(string dataDirectory, HistoryDatabase? database = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _database = database;
    }

    public BackupInspection Inspect(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry(ManifestEntry)
                ?? throw new InvalidDataException("备份包缺少清单文件");
            if (manifestEntry.Length is < 0 or > MaxManifestBytes)
            {
                throw new InvalidDataException("备份清单超过 1MB 上限");
            }
            var manifest = ReadManifest(manifestEntry)
                ?? throw new InvalidDataException("备份包清单无法解析");
            if (manifest.FormatVersion != CurrentFormatVersion)
            {
                return new BackupInspection(false, $"不支持的备份格式版本：{manifest.FormatVersion}");
            }

            ValidateEntries(archive, manifest);
            return new BackupInspection(true, "备份包完整，可恢复", manifest);
        }
        catch (Exception ex)
        {
            return new BackupInspection(false, $"备份包校验失败：{ex.Message}");
        }
    }

    public BackupManifest BackupTo(string destinationPath, string appVersion)
    {
        var target = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException("无法确定备份目标目录");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(Path.GetTempPath(), $"TranslationApp-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<BackupFileEntry>();
            var settingsSource = Path.Combine(_dataDirectory, SettingsEntry);
            if (File.Exists(settingsSource))
            {
                CopyAndHash(settingsSource, Path.Combine(staging, SettingsEntry), files);
            }

            if (_database is { IsAvailable: true })
            {
                var snapshot = Path.Combine(staging, HistoryEntry);
                _database.BackupTo(snapshot);
                files.Add(BuildEntry(snapshot, HistoryEntry));
            }

            var manifest = new BackupManifest(
                CurrentFormatVersion, DateTimeOffset.UtcNow, appVersion, files);
            File.WriteAllText(
                Path.Combine(staging, ManifestEntry),
                JsonSerializer.Serialize(manifest, JsonOptions));

            var partial = target + ".partial";
            File.Delete(partial);
            ZipFile.CreateFromDirectory(staging, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(partial, target, overwrite: true);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public BackupInspection RestoreFrom(string archivePath)
    {
        var inspection = Inspect(archivePath);
        if (!inspection.IsValid || inspection.Manifest is null)
        {
            return inspection;
        }

        var staging = Path.Combine(Path.GetTempPath(), $"TranslationApp-restore-{Guid.NewGuid():N}");
        var rollback = Path.Combine(Path.GetTempPath(), $"TranslationApp-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(rollback);

        var restored = new List<string>();
        try
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var file in inspection.Manifest.Files)
                {
                    var entry = archive.GetEntry(file.Path)
                        ?? throw new InvalidDataException($"备份包缺少文件：{file.Path}");
                    var destination = SafeCombinedPath(staging, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            foreach (var file in inspection.Manifest.Files)
            {
                var source = SafeCombinedPath(staging, file.Path);
                if (!File.Exists(source) || !HashMatches(source, file.Sha256))
                {
                    throw new InvalidDataException($"文件校验失败：{file.Path}");
                }

                var target = SafeCombinedPath(_dataDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var previous = SafeCombinedPath(rollback, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                if (File.Exists(target))
                {
                    File.Copy(target, previous, overwrite: true);
                }

                // 先登记再覆盖：若 File.Copy 只写了一半就失败，回滚也必须处理这个目标。
                restored.Add(file.Path);
                File.Copy(source, target, overwrite: true);
                if (string.Equals(file.Path, HistoryEntry, StringComparison.OrdinalIgnoreCase))
                {
                    SqliteConnection.ClearAllPools();
                    DeleteIfExists(target + "-wal");
                    DeleteIfExists(target + "-shm");
                }
            }

            return new BackupInspection(true, "恢复完成；设置与历史已替换", inspection.Manifest);
        }
        catch (Exception ex)
        {
            if (restored.Any(path => string.Equals(path, HistoryEntry, StringComparison.OrdinalIgnoreCase)))
            {
                SqliteConnection.ClearAllPools();
            }

            foreach (var relative in restored)
            {
                try
                {
                    var target = SafeCombinedPath(_dataDirectory, relative);
                    var previous = SafeCombinedPath(rollback, relative);
                    if (File.Exists(previous))
                    {
                        File.Copy(previous, target, overwrite: true);
                    }
                    else
                    {
                        DeleteIfExists(target);
                    }

                    if (string.Equals(relative, HistoryEntry, StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteIfExists(target + "-wal");
                        DeleteIfExists(target + "-shm");
                    }
                }
                catch
                {
                    // 回滚本身失败时保留原始异常，供用户据此排查。
                }
            }

            return new BackupInspection(false, $"恢复失败，已尝试回滚：{ex.Message}", inspection.Manifest);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(rollback);
        }
    }

    private static BackupManifest? ReadManifest(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd(), JsonOptions);
    }

    private static void ValidateEntries(ZipArchive archive, BackupManifest manifest)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SettingsEntry,
            HistoryEntry,
        };

        var files = manifest.Files
            ?? throw new InvalidDataException("备份清单缺少文件列表");
        if (files.Count > allowed.Count)
        {
            throw new InvalidDataException("备份清单文件数异常");
        }

        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == ManifestEntry) continue;
            if (entry.FullName.Length == 0 || entry.Name.Length == 0)
            {
                throw new InvalidDataException($"备份包包含不允许的目录条目：{entry.FullName}");
            }
            if (!allowed.Contains(entry.FullName) || !archivePaths.Add(entry.FullName))
            {
                throw new InvalidDataException($"备份包包含不允许或重复的文件：{entry.FullName}");
            }

            if (entry.Length is < 0 or > MaxEntryBytes)
            {
                throw new InvalidDataException($"文件大小异常：{entry.FullName}");
            }
            if (entry.Length > 4L * 1024 * 1024 && entry.CompressedLength > 0 &&
                entry.Length / Math.Max(1, entry.CompressedLength) > 200)
            {
                throw new InvalidDataException($"文件压缩比异常：{entry.FullName}");
            }

            totalBytes += entry.Length;
            if (totalBytes > MaxTotalEntryBytes)
            {
                throw new InvalidDataException("备份包解压后总大小超过上限");
            }
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) ||
                !allowed.Contains(file.Path) ||
                !string.Equals(Path.GetFileName(file.Path), file.Path, StringComparison.Ordinal) ||
                !manifestPaths.Add(file.Path))
            {
                throw new InvalidDataException($"备份清单包含不允许的文件：{file.Path}");
            }

            var entry = archive.GetEntry(file.Path)
                ?? throw new InvalidDataException($"备份包缺少文件：{file.Path}");
            if (entry.Length != file.SizeBytes || entry.Length is < 0 or > MaxEntryBytes)
            {
                throw new InvalidDataException($"文件大小与清单不符：{file.Path}");
            }
            if (!IsSha256(file.Sha256))
            {
                throw new InvalidDataException($"文件 SHA-256 格式错误：{file.Path}");
            }
        }

        if (!archivePaths.SetEquals(manifestPaths))
        {
            throw new InvalidDataException("备份包文件与清单不一致");
        }
    }

    private static void CopyAndHash(string source, string destination, ICollection<BackupFileEntry> files)
    {
        File.Copy(source, destination, overwrite: true);
        files.Add(BuildEntry(destination, Path.GetFileName(destination)));
    }

    private static BackupFileEntry BuildEntry(string path, string relativePath)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new BackupFileEntry(relativePath, stream.Length, hash);
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string SafeCombinedPath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"备份路径越界：{relative}");
        }

        return fullPath;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响备份结果。
        }
    }
}
