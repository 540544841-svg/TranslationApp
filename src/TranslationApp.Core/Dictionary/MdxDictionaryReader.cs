using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace TranslationApp.Core.Dictionary;

/// <summary>词典查询命中（词头 + 已清洗为纯文本的释义）。</summary>
/// <summary>
/// 一次词典命中。<paramref name="SourceName"/> = 命中的那份词典的显示名
/// （FR-055：多词典时用户要看得见是哪本给的，不然「按列表顺序取首个命中」不可解释）。
/// </summary>
public sealed record DictionaryQueryHit(string Word, string Definition, string? SourceName = null);

/// <summary>
/// MDX v3（后端为 SQLite）离线词典只读读取器（FR-049 最小可行）。
///
/// 支持面（spec §7 / §10）：**只解 v3 且资源块是 SQLite 的词典**；
/// v2 的二进制 BTree 索引、加密词条、图片词条一律明确报「暂不支持」，绝不做假解析。
///
/// 布局（zlib 自定界，因此不必精确知道头结构就能顺序走）：
/// 32 字节文件头（<c>MDX\x00</c> + 版本 + 块大小编码）→ 块 0 元数据 → 块 1 词表索引 → 块 2 起为资源（v3 = SQLite）。
/// SQLite 要求整文件随机访问，所以首个成功解析后把资源块落进缓存目录，之后按普通只读库打开；
/// 缓存名带源文件的时间戳与长度，换词典或词典更新都会自然重解，无需失效逻辑。
/// </summary>
public sealed class MdxDictionaryReader : IDisposable
{
    /// <summary>词典正文最长返回字符数（小窗卡片截断，全文交给界面展开）。</summary>
    public const int MaxDefinitionChars = 4000;

    /// <summary>不支持提示（唯一口径，设置页与小窗都用它）。</summary>
    public const string UnsupportedFormat = "暂不支持该格式（当前支持 MDX v3-SQLite）";

    private const string SqliteMagic = "SQLite format 3\0";
    private const int ProbeChunkBytes = 64 * 1024; // 判定资源类型只需看开头一截
    private const int MaxIndexVariants = 2;        // 索引后可能还有附加索引块，最多再往前翻这么多
    private const byte FallbackCompressBits = 6; // 头不可信时的试探默认值（绝大多数 mdx 都是 6）

    private readonly string _mdxPath;
    private readonly string _cacheDirectory;
    private SqliteConnection? _connection;

    private MdxDictionaryReader(string mdxPath, string cacheDirectory)
    {
        _mdxPath = mdxPath;
        _cacheDirectory = cacheDirectory;
    }

    /// <summary>词典文件的绝对路径（设置页据此算相对文件名，用于删除定位）。</summary>
    public string FilePath => _mdxPath;

    /// <summary>词典是否可用（失败原因见 <see cref="FailureReason"/>，风格与 <c>HistoryDatabase</c> 一致：不抛异常）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用原因（面向用户的中文；绝不含词条正文内容）。</summary>
    public string? FailureReason { get; private set; }

    /// <summary>显示名：元数据里的词典名，取不到时用文件名。</summary>
    public string DisplayName { get; private set; } = "";

    /// <summary>词条数（Term 表行数）。</summary>
    public int WordCount { get; private set; }

    public static MdxDictionaryReader Open(string mdxPath, string cacheDirectory)
    {
        var reader = new MdxDictionaryReader(mdxPath, cacheDirectory);
        reader.Initialize();
        return reader;
    }

    /// <summary>查词：先精确、再前缀（取最短词头，最接近用户点的那个词）；无命中返回 null。</summary>
    public DictionaryQueryHit? Query(string word)
    {
        if (_connection is null || string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        try
        {
            var normalized = NormalizeKey(word);
            if (normalized.Length == 0)
            {
                return null;
            }

            var entry = ExecuteEntryQuery(
                "SELECT EntryId, KeyText FROM Term WHERE KeyText = @key COLLATE NOCASE LIMIT 1;", normalized);
            entry ??= ExecuteEntryQuery(
                """
                SELECT EntryId, KeyText FROM Term
                WHERE KeyText LIKE @prefix ESCAPE '\' COLLATE NOCASE
                ORDER BY LENGTH(KeyText) ASC, KeyText ASC LIMIT 1;
                """,
                EscapeLike(normalized) + "%");
            if (entry is null)
            {
                return null;
            }

            var raw = ExecuteScalarObject(
                "SELECT Data FROM Definition WHERE EntryId = @id LIMIT 1;", entry.Value.EntryId);
            var definition = DefinitionTextStripper.Strip(DecodeText(raw));
            if (definition.Length == 0)
            {
                return null;
            }

            return new DictionaryQueryHit(
                entry.Value.KeyText,
                definition.Length > MaxDefinitionChars ? definition[..MaxDefinitionChars] + "…" : definition);
        }
        catch (SqliteException)
        {
            // 词典内部损坏 / 字段类型不符：一次查询失败不影响其它词典，也不该冒泡到翻译主流程
            return null;
        }
    }

    public void Dispose() => _connection?.Dispose();

    private (long EntryId, string KeyText)? ExecuteEntryQuery(string sql, string key)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@prefix", key);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1))
            : null;
    }

    private object? ExecuteScalarObject(string sql, long id)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar();
    }

    /// <summary>
    /// 词条编码在不同生成器之间不统一（UTF-8 / UTF-16LE 都有），BLOB 一律按 UTF-8 解，
    /// 出现替换符比例过高时改按 UTF-16LE 再解一次（v3 元数据声明的是后者）。
    /// </summary>
    private static string? DecodeText(object? raw) => raw switch
    {
        null or DBNull => null,
        string text => text,
        byte[] bytes => PreferUnicode(bytes),
        _ => raw.ToString(),
    };

    private static string PreferUnicode(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        var replacement = utf8.Count(c => c == '\uFFFD');
        if (replacement * 20 > utf8.Length && bytes.Length % 2 == 0)
        {
            var unicode = Encoding.Unicode.GetString(bytes);
            if (unicode.Count(c => c == '\uFFFD') < replacement)
            {
                return unicode;
            }
        }

        return utf8;
    }

    private void Initialize()
    {
        try
        {
            var bytes = File.ReadAllBytes(_mdxPath);
            DisplayName = Path.GetFileNameWithoutExtension(_mdxPath);
            if (!TryReadHeader(bytes, out var header, out var fatalReason))
            {
                FailureReason = fatalReason;
                return;
            }

            ParseMeta(ReadBlocks(bytes, header.CompressBits, out var sequentialEnd));

            var resource = ResolveSqliteResource(bytes, header, sequentialEnd);
            if (resource is null)
            {
                FailureReason = UnsupportedFormat;
                return;
            }

            var cached = TryLoadCached(resource.Offset, resource.Length)
                         ?? (TryExtract(resource, bytes, out var extracted) ? extracted : null);
            if (cached is null)
            {
                FailureReason = "词典缓存写入失败，请检查磁盘空间或临时目录权限";
                return;
            }

            OpenDatabase(cached);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException
                                      or InvalidOperationException or ArgumentException)
        {
            IsAvailable = false;
            FailureReason ??= "词典读取失败";
        }
    }

    // ---------------- 文件头 ----------------

    private sealed record FileHeader(byte CompressBits);

    private static bool TryReadHeader(byte[] bytes, out FileHeader header, out string? reason)
    {
        header = new FileHeader(FallbackCompressBits);
        reason = null;
        if (bytes.Length < 40 || bytes[0] != 'M' || bytes[1] != 'D' || bytes[2] != 'X' || bytes[3] != 0)
        {
            reason = "不是可识别的 mdx 文件";
            return false;
        }

        switch (BitConverter.ToUInt16(bytes, 4))
        {
            case 1:
                reason = "暂不支持该格式（v1 词典）";
                return false;
            case 2:
                reason = "暂不支持该格式（v2 二进制索引词典）";
                return false;
        }

        // 加密词典不给明确报错：解不出 SQLite 页头时统一落到「暂不支持该格式」，
        // 头里那几位在不同生成器之间含义不一致，硬判会把正常词典误杀。
        var compress = (byte)(bytes[6] >> 4);
        if (compress is < 1 or > 8)
        {
            compress = FallbackCompressBits;
        }

        // 资源块位置不靠头里的提示（字节 5-9 的 12 bit 偏移各家生成器给法不一，踩过一次把 & 0x0F
        // 写成 >> 4 就把 128 解成 240），改为按块头顺序走：元数据块 + 索引块之后的那一块就是资源。
        header = new FileHeader(compress);
        return true;
    }

    // ---------------- 块遍历 ----------------

    private static byte[] ReadBlocks(byte[] bytes, byte compressBits, out int sequentialEnd)
    {
        sequentialEnd = 32;
        if (!TryReadBlock(bytes, 32, compressBits, out var meta, out var next))
        {
            return [];
        }

        _ = TryReadBlock(bytes, next, compressBits, out _, out sequentialEnd);
        return meta;
    }

    private static bool TryReadBlock(byte[] bytes, int offset, byte compressBits, out byte[] payload, out int next)
    {
        payload = [];
        next = offset;
        var size = ReadBlockSize(bytes, offset, compressBits);
        if (size <= 0 || offset + 8 + size > bytes.Length)
        {
            return false;
        }

        // 压缩与否按生成器而异：先试 zlib，失败即按未压缩块原样取
        if (!TryDecompress(bytes, offset + 8, size, out payload))
        {
            payload = bytes.AsSpan(offset + 8, size).ToArray();
        }

        next = offset + 8 + size;
        return true;
    }

    /// <summary>
    /// 块头 8 字节的解码：<c>length = (1 &lt;&lt; 字节 0 低 4 位) + (12bit 位移量 &lt;&lt; exp)</c>，
    /// 其中 12 bit 取自字节 1 的低 4 位与字节 2；字节 4-7 是未压缩长度（只作参考，块边界一律以压缩长度推进）。
    /// 字节 0 为 0 是「没有更多块」的哨兵，因此块长至少 2，低 4 位取 1..5。
    /// </summary>
    private static int ReadBlockSize(byte[] bytes, int offset, byte compressBits)
    {
        if (offset < 0 || offset + 8 > bytes.Length || bytes[offset] == 0)
        {
            return -1;
        }

        var stored = ((bytes[offset + 1] & 0x0F) << 8) | bytes[offset + 2];
        var baseSize = 1 << (bytes[offset] & 0x0F);
        var size = baseSize + (stored << compressBits);
        return size > 0 ? size : -1;
    }

    /// <summary>资源块的数据起点与长度（块头给出）。</summary>
    private sealed record ResourceRef(int Offset, int Length);

    /// <summary>
    /// 资源块 = 元数据块与索引块之后第一处带 SQLite 页头的块；允许中间再多容 <see cref="MaxIndexVariants"/>
    /// 块（部分生成器在索引后还塞一份附加索引）。
    /// 真正的「能不能用」判据在打开库之后：SQLite 里必须有 <c>Term</c> 表（v3-SQLite 的硬特征），
    /// 只有 BTree 词表的 v2 词典到这里就会以「词典结构无法识别」被拒，不会被假装解析。
    /// </summary>
    private static ResourceRef? ResolveSqliteResource(byte[] bytes, FileHeader header, int sequentialEnd)
    {
        var pos = sequentialEnd;
        for (var scanned = 0; scanned <= MaxIndexVariants && pos + 8 <= bytes.Length; scanned++)
        {
            if (ProbeSqlite(bytes, pos, header.CompressBits, out var resource))
            {
                return resource;
            }

            var size = ReadBlockSize(bytes, pos, header.CompressBits);
            if (size <= 0)
            {
                return null;
            }

            pos += 8 + Math.Min(size, bytes.Length - pos - 8);
        }

        return null;
    }

    /// <summary>块头里给出的压缩长度（供单测把编码与解码钉死）。</summary>
    public static int ReadStoredBlockLength(byte[] bytes, int blockOffset, byte compressBits) =>
        ReadBlockSize(bytes, blockOffset, compressBits);

    /// <summary>判断某个块头位置的资源是不是 SQLite（与内部走块逻辑同一入口，供单测把格式编码钉死）。</summary>
    public static bool ProbeBlockIsSqlite(byte[] bytes, int blockOffset, byte compressBits) =>
        ProbeSqlite(bytes, blockOffset, compressBits, out _);

    /// <summary>
    /// 判断某个块头位置的资源是不是 SQLite：先看未压缩页头，再按块头给出的长度试 zlib
    /// （生成器之间压缩与否并不统一）。
    /// </summary>
    private static bool ProbeSqlite(byte[] bytes, int blockOffset, byte compressBits, out ResourceRef? resource)
    {
        resource = null;
        var size = ReadBlockSize(bytes, blockOffset, compressBits);
        if (size <= 0 || blockOffset + 8 + 16 > bytes.Length)
        {
            return false;
        }

        var start = blockOffset + 8;
        var available = Math.Min(size, bytes.Length - start);
        if (HasMagicAt(bytes, start))
        {
            resource = new ResourceRef(start, available);
            return true;
        }

        if (!TryDecompress(bytes, start, available, out var sample, ProbeChunkBytes) || !HasMagicAt(sample, 0))
        {
            return false;
        }

        resource = new ResourceRef(start, available);
        return true;
    }

    private static bool HasMagicAt(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + SqliteMagic.Length > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < SqliteMagic.Length; i++)
        {
            if (bytes[offset + i] != (byte)SqliteMagic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecompress(byte[] bytes, int offset, int length, out byte[] payload) =>
        TryDecompress(bytes, offset, length, out payload, int.MaxValue);

    /// <summary>
    /// 解压一段 zlib 流，产出最多 <paramref name="limit"/> 字节（探测只看开头一截时用它）。
    /// 输入必须是**区间视图**而不是整数组：DeflateStream 会预读，越界就会把 zlib 尾部校验吃掉，
    /// 后续 <c>ZLibStream</c> 读不到 adler-32 便判定失败（真实词典与自造夹具都在这里栽过）。
    /// </summary>
    private static bool TryDecompress(byte[] bytes, int offset, int length, out byte[] payload, int limit)
    {
        payload = [];
        if (length < 2)
        {
            return false;
        }

        try
        {
            using var input = new MemorySegmentStream(bytes, offset, length);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (output.Length < limit)
            {
                var room = (int)Math.Min(buffer.Length, (long)limit - output.Length);
                var read = zlib.Read(buffer, 0, room);
                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
            }

            payload = output.ToArray();
            return payload.Length > 0;
        }
        catch (InvalidDataException)
        {
            return false; // 不是 zlib 流 → 由调用方按「未压缩块」处理
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>只暴露 <c>[offset, offset+length)</c> 区间的只读流视图（块边界由 mdx 块头给出）。</summary>
    private sealed class MemorySegmentStream : Stream
    {
        private readonly byte[] _source;
        private readonly long _start;
        private readonly long _end;
        private long _position;

        public MemorySegmentStream(byte[] source, int offset, int length)
        {
            _source = source;
            _start = offset;
            _end = offset + length;
            _position = offset;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _end - _start;

        public override long Position
        {
            get => _position - _start;
            set => _position = Math.Clamp(_start + value, _start, _end);
        }

        public override int Read(byte[] buffer, int index, int count) =>
            Read(buffer.AsSpan(index, count));

        public override int Read(Span<byte> buffer)
        {
            var available = (int)Math.Min(buffer.Length, _end - _position);
            if (available <= 0)
            {
                return 0;
            }

            _source.AsSpan((int)_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => Position = offset,
            SeekOrigin.End => Position = Length + offset,
            _ => Position += offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------- 元数据 ----------------

    private void ParseMeta(byte[] meta)
    {
        if (meta.Length == 0)
        {
            return;
        }

        var text = DecodeMeta(meta);

        // 只按 \n 分行：UTF-16 的换行本身就是 "\n\0"，把 '\0' 也当分隔符会把空行吃掉、行号整体前移
        var lines = text
            .Split('\n')
            .Select(line => line.Trim('\0', ' ', '\t', '\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        // 行序：0 文本编码、1 索引编码、2 格式版本、3 词典名、4 作者、5 版权
        if (lines.Length > 3 && lines[3].Length is > 0 and < 120)
        {
            DisplayName = lines[3];
        }
    }

    /// <summary>
    /// 元数据块多数是 UTF-16LE（ASCII 字符的偶数字节非 0、奇数字节恒为 0），少数生成器写 UTF-8。
    /// 判据只看「奇数字节里 0 的占比」：UTF-16LE 的 ASCII 段接近全 0，UTF-8 的 ASCII 段接近全非 0。
    /// </summary>
    private static string DecodeMeta(byte[] meta)
    {
        var limit = Math.Min(meta.Length - meta.Length % 2, 64);
        var oddNulls = 0;
        for (var i = 1; i < limit; i += 2)
        {
            if (meta[i] == 0)
            {
                oddNulls++;
            }
        }

        var oddPairs = Math.Max(1, limit / 2);
        return oddNulls * 2 > oddPairs
            ? Encoding.Unicode.GetString(meta)
            : Encoding.UTF8.GetString(meta);
    }

    // ---------------- 缓存与打开 ----------------

    private string CachePath(long sourceOffset, int length)
    {
        var info = new FileInfo(_mdxPath);
        var key = $"{info.Name}-{info.Length}-{info.LastWriteTimeUtc.Ticks}-{sourceOffset}-{length}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            key = key.Replace(invalid, '_');
        }

        return Path.Combine(_cacheDirectory, key + ".sqlite");
    }

    private string? TryLoadCached(long sourceOffset, int length)
    {
        var path = CachePath(sourceOffset, length);
        return File.Exists(path) && new FileInfo(path).Length >= 512 ? path : null;
    }

    /// <summary>把资源块读成一份 SQLite 字节（未压缩即切片，压缩则解压）。</summary>
    private byte[]? ReadDatabase(byte[] bytes, ResourceRef resource)
    {
        if (HasMagicAt(bytes, resource.Offset))
        {
            return bytes.AsSpan(resource.Offset, Math.Max(0, Math.Min(resource.Length, bytes.Length - resource.Offset))).ToArray();
        }

        var available = Math.Min(resource.Length, Math.Max(0, bytes.Length - resource.Offset));
        return TryDecompress(bytes, resource.Offset, available, out var payload) ? payload : null;
    }

    /// <summary>把资源块落成缓存文件；词典更新（长度/时间戳变化）会自然换 key，无需失效逻辑。</summary>
    private bool TryExtract(ResourceRef resource, byte[] bytes, out string path)
    {
        path = CachePath(resource.Offset, resource.Length);
        try
        {
            var database = ReadDatabase(bytes, resource);
            if (database is null or { Length: < 512 })
            {
                return false;
            }

            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllBytes(path, database);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void OpenDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        };

        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            var hasTerm = connection.ExecuteScalar<long?>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Term';");
            if (hasTerm != 1)
            {
                connection.Dispose();
                FailureReason = "词典结构无法识别（缺少 Term 表）";
                return;
            }

            WordCount = (int)connection.ExecuteScalar<long>("SELECT COUNT(*) FROM Term;");
            _connection = connection;
            IsAvailable = true;
            FailureReason = null;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            FailureReason = UnsupportedFormat;
        }
    }

    private static string NormalizeKey(string word)
    {
        var trimmed = word.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        // 只剥首尾的词典标记与控制字符，不动词内字符（URL 里的 & ? 等要原样留给 SQL）
        var start = 0;
        var end = trimmed.Length;
        while (start < end && IsMarkup(trimmed[start]))
        {
            start++;
        }

        while (end > start && IsMarkup(trimmed[end - 1]))
        {
            end--;
        }

        return trimmed[start..end].Trim();
    }

    private static bool IsMarkup(char c) => c is '\u0001' or '\u0002' or '\u0000' or '\n' or '\r' or '\t' or ' ';

    private static string EscapeLike(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '%' or '_' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
