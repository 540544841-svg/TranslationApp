using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using TranslationApp.Core.Dictionary;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>
/// mdx 词典读取器（FR-049 最小可行）单测。
///
/// 手上没有可分发的真实词典样本（版权与体积都不合适），因此按 spec §7 的约定
/// **自造一个 v3-SQLite 结构的 mdx**：真 SQLite 字节 + 真块头 + 真 zlib，
/// 让「块遍历 / 资源识别 / 查询 / 清洗」整条路径都跑在真实格式上，
/// 而不是把解析器对着假想的结构写。
/// </summary>
public sealed class MdxDictionaryReaderTests : IDisposable
{
    private readonly string _root = MakeRoot();

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TranslationApp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private string Cache => Path.Combine(_root, "cache");

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

    // ---------------- 正常路径 ----------------

    [Fact]
    public void 打开_v3_SQLite_词典_可按词头精确查词并清洗_HTML()
    {
        var path = WriteMdx(CreateDatabase(("apple", "<b>n.</b> 苹果 &amp; 果树", 1), ("appletree", "小树", 2)));

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.True(reader.IsAvailable, reader.FailureReason);
        Assert.Equal("测试词典", reader.DisplayName);
        Assert.Equal(2, reader.WordCount);

        var hit = reader.Query("apple");
        Assert.NotNull(hit);
        Assert.Equal("apple", hit!.Word);
        Assert.Equal("n. 苹果 & 果树", hit.Definition);
    }

    [Fact]
    public void 查词忽略大小写_精确未命中时走前缀取最短词头()
    {
        var path = WriteMdx(CreateDatabase(
            ("APP", "大写词头", 1),
            ("application", "应用", 2),
            ("applications", "应用（复数）", 3)));

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.True(reader.IsAvailable, reader.FailureReason);

        Assert.Equal("大写词头", reader.Query("app")?.Definition);
        Assert.Equal("应用", reader.Query("applic")?.Definition);   // 前缀 → 最短的 application
        Assert.Null(reader.Query("zzz"));
    }

    [Fact]
    public void 词条存为_BLOB_时按编码解码_RTFC_控制字被剥掉()
    {
        // \uNNNN + 空格分隔符 = 正文汉字（26524=果、23376=子）；组结束 } 算换行
        var rtf = @"{\rtf1\ansi \u26524 \u23376}";
        Assert.Equal("果子", DefinitionTextStripper.StripRtf(rtf));
        var path = WriteMdx(CreateDatabaseRtf(("pear", rtf, 1)));

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.True(reader.IsAvailable, reader.FailureReason);

        Assert.Equal("果子", reader.Query("pear")?.Definition);
    }

    [Theory]
    [InlineData(true)]   // 资源块 zlib 压缩
    [InlineData(false)]  // 资源块不压缩（常见生成器行为）
    public void 顺序走块定位资源_压缩与未压缩都支持(bool resourceZlib)
    {
        var db = CreateDatabase(("kiwi", "奇异果", 1));
        var path = WriteMdx(db, resourceZlib: resourceZlib);

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.True(reader.IsAvailable, reader.FailureReason);
        Assert.Equal("奇异果", reader.Query("kiwi")?.Definition);
    }

    [Fact]
    public void 二次打开命中缓存_结果一致()
    {
        var path = WriteMdx(CreateDatabase(("mango", "芒果", 1)));

        using (var first = MdxDictionaryReader.Open(path, Cache))
        {
            Assert.True(first.IsAvailable, first.FailureReason);
        }

        Assert.NotEmpty(Directory.GetFiles(Cache, "*.sqlite"));
        using var second = MdxDictionaryReader.Open(path, Cache);
        Assert.True(second.IsAvailable, second.FailureReason);
        Assert.Equal("芒果", second.Query("mango")?.Definition);
    }

    // ---------------- 明确不支持（不做假解析） ----------------

    [Fact]
    public void v1_词典明确报暂不支持()
    {
        var path = WriteMdx(CreateDatabase(("x", "y", 1)), version: 1);

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.False(reader.IsAvailable);
        Assert.Contains("v1", reader.FailureReason);
    }

    [Fact]
    public void v2_词典明确报暂不支持()
    {
        var path = WriteMdx(CreateDatabase(("x", "y", 1)), version: 2);

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.False(reader.IsAvailable);
        Assert.Contains("v2", reader.FailureReason);
    }

    [Fact]
    public void 不是_mdx_文件时报错而不是崩溃()
    {
        var path = Path.Combine(_root, "junk.mdx");
        File.WriteAllBytes(path, new byte[64]);

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.False(reader.IsAvailable);
        Assert.Equal("不是可识别的 mdx 文件", reader.FailureReason);
    }

    [Fact]
    public void 缺少_Term_表时报词典结构无法识别()
    {
        var path = WriteMdx(CreateEmptyDatabase());

        using var reader = MdxDictionaryReader.Open(path, Cache);
        Assert.False(reader.IsAvailable);
        Assert.Contains("Term", reader.FailureReason);
    }

    // ---------------- 夹具：自造 v3-SQLite mdx ----------------

    [Fact]
    public void 夹具的块头编码与读取侧解码互逆()
    {
        var db = CreateDatabase(("fig", "无花果", 1));
        var block = Block(db, db.Length, 6);
        Assert.Equal(block.Length - 8, MdxDictionaryReader.ReadStoredBlockLength(block, 0, 6));
        Assert.True(MdxDictionaryReader.ProbeBlockIsSqlite(block, 0, 6));

        var zlibBlock = Block(Zlib(db), db.Length, 6);
        Assert.Equal(zlibBlock.Length - 8, MdxDictionaryReader.ReadStoredBlockLength(zlibBlock, 0, 6));
        Assert.True(MdxDictionaryReader.ProbeBlockIsSqlite(zlibBlock, 0, 6));
    }

    private byte[] CreateDatabase(params (string Key, string Definition, long Id)[] entries) =>
        BuildDatabase(entries.Select(e => (e.Key, e.Definition, e.Id, blob: false)).ToArray());

    private byte[] CreateDatabaseRtf(params (string Key, string Definition, long Id)[] entries) =>
        BuildDatabase(entries.Select(e => (e.Key, e.Definition, e.Id, blob: true)).ToArray());

    private byte[] BuildDatabase((string Key, string Definition, long Id, bool Blob)[] entries)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.sqlite");
        // Pooling=false：句柄随 Dispose 立刻归还，Windows 上才能马上读字节并删临时文件
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        try
        {
            using (var connection = new SqliteConnection(builder))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE Term ("Index" INTEGER PRIMARY KEY, KeyText TEXT, EntryId INTEGER);
                    CREATE TABLE Definition (EntryId INTEGER PRIMARY KEY, Data BLOB);
                    """;
                create.ExecuteNonQuery();

                foreach (var (key, definition, id, blob) in entries)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO Term (\"Index\", KeyText, EntryId) VALUES (@index, @key, @id);" +
                                         " INSERT INTO Definition (EntryId, Data) VALUES (@id, @data);";
                    insert.Parameters.AddWithValue("@index", id);
                    insert.Parameters.AddWithValue("@key", key);
                    insert.Parameters.AddWithValue("@id", id);
                    insert.Parameters.AddWithValue("@data", blob ? Encoding.UTF8.GetBytes(definition) : definition);
                    insert.ExecuteNonQuery();
                }
            }

            return File.ReadAllBytes(path); // 真 SQLite 字节（页内尾部零填充，SQLite 自己会忽略）
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private byte[] CreateEmptyDatabase()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Foo (Id INTEGER);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private string WriteMdx(
        byte[] database,
        byte compressBits = 6,
        bool resourceZlib = true,
        ushort version = 3)
    {
        Directory.CreateDirectory(_root);
        var metaRaw = Encoding.Unicode.GetBytes("UTF-8\nUTF-8\n3.0.0.0\n测试词典\nSpeedy\nMIT\n\n");
        var meta = Zlib(metaRaw);
        var index = Encoding.Unicode.GetBytes("index-block-placeholder");
        var resource = resourceZlib ? Zlib(database) : database;

        var metaBlock = Block(meta, metaRaw.Length, compressBits);
        var indexBlock = Block(index, index.Length, compressBits);
        var resourceBlock = Block(resource, database.Length, compressBits);

        using var stream = new MemoryStream();
        stream.Write(Header(version, compressBits));
        stream.Write(metaBlock);
        stream.Write(indexBlock);
        stream.Write(resourceBlock);

        var path = Path.Combine(_root, $"fixture-{Guid.NewGuid():N}.mdx");
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static byte[] Header(ushort version, byte compressBits)
    {
        var header = new byte[32];
        header[0] = (byte)'M';
        header[1] = (byte)'D';
        header[2] = (byte)'X';
        header[4] = (byte)version;
        header[6] = (byte)(compressBits << 4); // 块大小的指数（读取侧唯一用到的头字段）
        return header;
    }

    private static byte[] Block(byte[] payload, int rawLength, byte compressBits)
    {
        var (_, _, padded) = EncodedBlockLength(payload.Length, compressBits);
        var header = BlockHeader(padded, rawLength, compressBits);
        var block = new byte[header.Length + padded];
        Buffer.BlockCopy(header, 0, block, 0, header.Length);
        Buffer.BlockCopy(payload, 0, block, header.Length, payload.Length);
        return block;
    }

    /// <summary>
    /// 块长度编码：<c>length = (1 &lt;&lt; low) + (blockBits &lt;&lt; exp)</c>，<c>low ∈ 0..5</c>、<c>blockBits ≤ 4095</c>。
    /// 不能精确表达时补零到最近的可表达长度（zlib 流自定界、SQLite 忽略尾部多余字节，两种情况都安全）。
    /// 读取侧 <c>MdxDictionaryReader.ReadBlockSize</c> 必须取同一组最小 <c>low</c>，否则两边会解出不同的值。
    /// </summary>
    private static (int BlockBits, int Low, int Padded) EncodedBlockLength(int length, byte compressBits)
    {
        var shift = 1 << compressBits;
        for (var extra = 0; extra <= shift * 2; extra++)
        {
            var padded = length + extra;
            for (var low = 1; low <= 5; low++)
            {
                var baseSize = 1 << low;
                if (padded <= baseSize || (padded - baseSize) % shift != 0)
                {
                    continue;
                }

                var blockBits = (padded - baseSize) / shift;
                if (blockBits <= 0x0FFF)
                {
                    return (blockBits, low, padded);
                }
            }
        }

        throw new InvalidOperationException($"无法编码块长度 {length}");
    }

    private static byte[] BlockHeader(int length, int uncompressedLength, byte compressBits)
    {
        var (blockBits, low, _) = EncodedBlockLength(length, compressBits);
        var header = new byte[8];
        header[0] = (byte)low;
        header[1] = (byte)((blockBits >> 8) & 0x0F);
        header[2] = (byte)(blockBits & 0xFF);
        header[4] = (byte)(uncompressedLength & 0xFF);
        header[5] = (byte)((uncompressedLength >> 8) & 0xFF);
        header[6] = (byte)((uncompressedLength >> 16) & 0xFF);
        header[7] = (byte)((uncompressedLength >> 24) & 0xFF);
        return header;
    }

    /// <summary>
    /// 用 <see cref="ZLibStream"/> 产出真 zlib 流（含头与 adler-32）。
    /// 手写 0x78/0x9C + <see cref="DeflateStream"/> 走不通：.NET 的 ZLibStream 判定格式不止看那两个字节，
    /// 自造头实测一律报「unsupported compression method」，因此夹具直接用同一套库生成，避免与实现走两条路。
    /// </summary>
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
