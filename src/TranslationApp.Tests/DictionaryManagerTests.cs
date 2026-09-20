using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using TranslationApp.Core.Dictionary;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// 词典目录管理（FR-049）：导入必须「解析得通才留下」，删除只允许词典目录内的相对路径，
/// 扫描必须惰性（开机不做 IO），关闭开关后一律不查。
/// </summary>
public sealed class DictionaryManagerTests : IDisposable
{
    private readonly string _root = MakeRoot();

    private string Dicts => Path.Combine(_root, "dicts");

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TranslationApp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    [Fact]
    public void 导入可用词典后能查到并出现在列表()
    {
        var manager = new DictionaryManager(Dicts, Path.Combine(_root, "cache"));
        var source = WriteExternalDict(("orange", "橘子", 1));

        var imported = manager.Import(source);

        Assert.True(imported.Ok, imported.Reason);
        Assert.Equal("orange.mdx", imported.FileName);
        Assert.Equal("橘子", manager.Query("orange")?.Definition);

        var item = Assert.Single(manager.List());
        Assert.True(item.IsAvailable);
        Assert.Equal("测试词典", item.DisplayName);
        Assert.Equal(1, item.WordCount);

        // 设置页的「测试查询」按文件名点名查，不靠顺序取首个命中
        Assert.Equal("橘子", manager.QueryFrom(item.FileName, "orange")?.Definition);
        Assert.Null(manager.QueryFrom("no-such.mdx", "orange"));
    }

    [Fact]
    public void 解析不了的导入被拒绝且不留半成品文件()
    {
        var manager = new DictionaryManager(Dicts, Path.Combine(_root, "cache"));
        var junk = Path.Combine(_root, "junk.mdx");
        File.WriteAllBytes(junk, new byte[128]);

        var imported = manager.Import(junk);

        Assert.False(imported.Ok);
        Assert.NotNull(imported.Reason);
        Assert.Empty(Directory.EnumerateFiles(Dicts));
        Assert.Empty(manager.List());
    }

    [Fact]
    public void 同名词典不覆盖已有文件而是另存一份()
    {
        var manager = new DictionaryManager(Dicts, Path.Combine(_root, "cache"));
        var source = WriteExternalDict(("a", "甲", 1));
        Assert.True(manager.Import(source).Ok);

        var again = manager.Import(source);

        Assert.True(again.Ok, again.Reason);
        Assert.NotEqual("a.mdx", again.FileName);
        Assert.Equal(2, manager.Count);
    }

    [Fact]
    public void 删除只接受词典目录内的相对路径()
    {
        var manager = new DictionaryManager(Dicts, Path.Combine(_root, "cache"));
        manager.Import(WriteExternalDict(("b", "乙", 1)));
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "keep me");

        Assert.False(manager.Delete("../outside.txt"));
        Assert.True(File.Exists(outside));

        Assert.True(manager.Delete("b.mdx"));
        Assert.True(File.Exists(outside));   // 越界路径既没删成，也不该伤及别的文件
        Assert.Empty(manager.List());
    }

    [Fact]
    public void 关闭词典开关时不查词但列表照常可见()
    {
        var manager = new DictionaryManager(Dicts, Path.Combine(_root, "cache"), () => false);
        manager.Import(WriteExternalDict(("c", "丙", 1)));

        // 列表可见：设置页要能说明「词典装着，只是功能关了」；查询与懒扫描一律被门控挡住
        Assert.Null(manager.Query("c"));
        Assert.Equal(1, manager.List().Count);
    }

    [Fact]
    public void 词典目录不存在时列表为空而不是抛异常()
    {
        var manager = new DictionaryManager(Path.Combine(_root, "missing"), Path.Combine(_root, "cache"));

        Assert.Empty(manager.List());
        Assert.Null(manager.Query("anything"));
    }

    // ---------------- 夹具：一份最小可用的 v3-SQLite mdx（外部文件，用于导入） ----------------

    private string WriteExternalDict(params (string Key, string Definition, long Id)[] entries)
    {
        var db = BuildDatabase(entries);
        var path = Path.Combine(_root, $"{entries[0].Key}.mdx");
        File.WriteAllBytes(path, BuildMdx(db));
        return path;
    }

    private static byte[] BuildDatabase((string Key, string Definition, long Id)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dict-{Guid.NewGuid():N}.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // 句柄立刻归还，Windows 上才能马上读字节并删除
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE Term ("Index" INTEGER PRIMARY KEY, KeyText TEXT, EntryId INTEGER);
                CREATE TABLE Definition (EntryId INTEGER PRIMARY KEY, Data BLOB);
                """;
            create.ExecuteNonQuery();

            foreach (var (key, definition, id) in entries)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO Term (\"Index\", KeyText, EntryId) VALUES (@index, @key, @id);" +
                                     " INSERT INTO Definition (EntryId, Data) VALUES (@id, @data);";
                insert.Parameters.AddWithValue("@index", id);
                insert.Parameters.AddWithValue("@key", key);
                insert.Parameters.AddWithValue("@id", id);
                insert.Parameters.AddWithValue("@data", definition);
                insert.ExecuteNonQuery();
            }
        }

        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    /// <summary>块头编码与 <see cref="MdxDictionaryReader"/> 的解码一致（细节见 MdxDictionaryReaderTests 的同名夹具）。</summary>
    private static byte[] BuildMdx(byte[] database)
    {
        var meta = Zlib(Encoding.Unicode.GetBytes("UTF-8\nUTF-8\n3.0\n测试词典\nSpeedy\nMIT\n"));
        var index = Encoding.Unicode.GetBytes("index-block");
        var resource = Zlib(database);

        using var stream = new MemoryStream();
        var header = new byte[32];
        header[0] = (byte)'M';
        header[1] = (byte)'D';
        header[2] = (byte)'X';
        header[4] = 3;
        header[6] = 6 << 4;
        stream.Write(header);
        stream.Write(Block(meta));
        stream.Write(Block(index));
        stream.Write(Block(resource));
        return stream.ToArray();
    }

    private static byte[] Block(byte[] payload, int? rawLength = null, byte compressBits = 6)
    {
        var shift = 1 << compressBits;
        var (low, padded) = EncodeLength(payload.Length, shift);
        var blockBits = (padded - (1 << low)) >> compressBits;
        var header = new byte[8];
        header[0] = (byte)low;
        header[1] = (byte)((blockBits >> 8) & 0x0F);
        header[2] = (byte)(blockBits & 0xFF);
        var raw = rawLength ?? padded;
        header[4] = (byte)(raw & 0xFF);
        header[5] = (byte)((raw >> 8) & 0xFF);
        header[6] = (byte)((raw >> 16) & 0xFF);
        header[7] = (byte)((raw >> 24) & 0xFF);
        var block = new byte[8 + padded];
        Buffer.BlockCopy(header, 0, block, 0, 8);
        Buffer.BlockCopy(payload, 0, block, 8, payload.Length);
        return block;
    }

    private static (int Low, int Padded) EncodeLength(int length, int shift)
    {
        for (var extra = 0; extra <= shift * 2; extra++)
        {
            for (var low = 1; low <= 5; low++)
            {
                var baseSize = 1 << low;
                var padded = length + extra;
                var blockBits = (padded - baseSize) / shift;
                if (padded > baseSize && (padded - baseSize) % shift == 0 && blockBits <= 0x0FFF)
                {
                    return (low, padded);
                }
            }
        }

        throw new InvalidOperationException($"无法编码块长度 {length}");
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
