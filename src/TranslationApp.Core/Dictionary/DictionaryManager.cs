namespace TranslationApp.Core.Dictionary;

/// <summary>词典清单项（设置页列表用）：不可用项带明确的中文原因，不静默隐藏。</summary>
public sealed record DictionaryInfo(string FileName, string DisplayName, bool IsAvailable, int WordCount, string? Reason);

/// <summary>导入结果：Ok=false 时 Reason 说明原因（并已把复制进来的文件清掉，不留半成品）。</summary>
public sealed record DictionaryImportResult(bool Ok, string? FileName, string? Reason);

/// <summary>
/// mdx 词典管理（FR-049）：一个目录 = 一份词典，按文件名顺序查询、取首个命中。
/// 词典只在本地读取，不产生任何网络请求；打开失败不影响翻译主流程（各项各自带原因）。
/// 扫描是**惰性的**（首次查询/列示时才做）：解析 mdx 要把资源块落进缓存，
/// 开机路径上绝不该做这件事（spec 红线：任何新入口不给译文落地增加 >50ms 的等待）。
/// </summary>
public sealed class DictionaryManager : IDisposable
{
    private readonly string _dictionaryDirectory;
    private readonly string _cacheDirectory;
    private readonly Func<bool>? _enabledProvider;
    private readonly List<MdxDictionaryReader> _readers = [];
    private bool _loaded;

    public DictionaryManager(string dictionaryDirectory, string? cacheDirectory = null, Func<bool>? enabledProvider = null)
    {
        _dictionaryDirectory = dictionaryDirectory;
        _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "TranslationApp", "dict-cache");
        _enabledProvider = enabledProvider;
    }

    /// <summary>已识别的词典数（含不可用项，设置页要能看见「为什么不可用」）。</summary>
    public int Count
    {
        get
        {
            EnsureLoaded();
            return _readers.Count;
        }
    }

    /// <summary>重新扫描目录并重建读取器（导入/删除后调用）。</summary>
    public void Reload()
    {
        foreach (var reader in _readers)
        {
            reader.Dispose();
        }

        _readers.Clear();
        _loaded = true;
        if (!Directory.Exists(_dictionaryDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_dictionaryDirectory, "*.mdx", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            _readers.Add(MdxDictionaryReader.Open(file, _cacheDirectory));
        }
    }

    /// <summary>首次使用才扫描；关闭词典时连扫描都不做。</summary>
    private void EnsureLoaded()
    {
        if (_loaded || _enabledProvider?.Invoke() == false)
        {
            return;
        }

        Reload();
    }

    public IReadOnlyList<DictionaryInfo> List()
    {
        EnsureLoaded();
        return _readers
            .Select(reader => new DictionaryInfo(
                Path.GetRelativePath(_dictionaryDirectory, reader.FilePath),
                reader.DisplayName, reader.IsAvailable, reader.WordCount, reader.FailureReason))
            .ToArray();
    }

    /// <summary>逐词典查询，返回首个命中（多词典时顺序即优先级）。</summary>
    public DictionaryQueryHit? Query(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || _enabledProvider?.Invoke() == false)
        {
            return null;
        }

        EnsureLoaded();
        foreach (var reader in _readers)
        {
            if (!reader.IsAvailable)
            {
                continue;
            }

            var hit = reader.Query(word);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// 只查某一份词典（设置页「测试查询」要能看清每份词典各自命中什么，而不是被首个命中挡住）。
    /// <paramref name="fileName"/> 是 <see cref="List"/> 给出的相对路径；找不到该文件或它不可用 → null。
    /// </summary>
    public DictionaryQueryHit? QueryFrom(string fileName, string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        EnsureLoaded();
        var target = _readers.FirstOrDefault(r => string.Equals(
            Path.GetRelativePath(_dictionaryDirectory, r.FilePath), fileName, StringComparison.OrdinalIgnoreCase));
        return target is { IsAvailable: true } ? target.Query(word) : null;
    }

    /// <summary>导入 = 复制进词典目录并立即解析校验；解析不出来即回滚删除。</summary>
    public DictionaryImportResult Import(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            return new DictionaryImportResult(false, null, "文件不存在");
        }

        var fileName = Path.GetFileName(sourceFilePath);
        if (!fileName.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
        {
            return new DictionaryImportResult(false, null, MdxDictionaryReader.UnsupportedFormat);
        }

        Directory.CreateDirectory(_dictionaryDirectory);
        var target = UniqueTargetPath(fileName);
        try
        {
            File.Copy(sourceFilePath, target, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DictionaryImportResult(false, null, "复制失败：" + ex.GetType().Name);
        }

        var probe = MdxDictionaryReader.Open(target, _cacheDirectory);
        var ok = probe.IsAvailable;
        var reason = probe.FailureReason;
        probe.Dispose();
        if (!ok)
        {
            TryDelete(target);
            return new DictionaryImportResult(false, null, reason ?? MdxDictionaryReader.UnsupportedFormat);
        }

        Reload();
        return new DictionaryImportResult(true, Path.GetFileName(target), null);
    }

    /// <summary>删除词典（文件名 = 列表项的相对路径，越界路径一律拒绝）。</summary>
    public bool Delete(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var root = Path.GetFullPath(_dictionaryDirectory + Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            return false;
        }

        if (!TryDelete(target))
        {
            return false;
        }

        Reload();
        return true;
    }

    public void Dispose()
    {
        foreach (var reader in _readers)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    private string UniqueTargetPath(string fileName)
    {
        var target = Path.Combine(_dictionaryDirectory, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        // 重名不覆盖用户已有词典：加序号另存（导入是低频动作，宁可多一个文件也不悄悄替换）
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; index < 100; index++)
        {
            target = Path.Combine(_dictionaryDirectory, $"{stem} ({index}){Path.GetExtension(fileName)}");
            if (!File.Exists(target))
            {
                return target;
            }
        }

        return Path.Combine(_dictionaryDirectory, $"{stem}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
