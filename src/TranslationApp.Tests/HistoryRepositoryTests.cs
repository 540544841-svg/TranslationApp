using TranslationApp.Core.History;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>历史与生词本仓储测试（FR-014/015）：使用临时数据库文件，测后删除。</summary>
public sealed class HistoryRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly HistoryDatabase _database;
    private readonly HistoryRepository _history;
    private readonly VocabularyRepository _vocabulary;

    public HistoryRepositoryTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"ta-test-{Guid.NewGuid():N}.db");
        _database = new HistoryDatabase(_databasePath);
        _database.Initialize();
        _history = new HistoryRepository(_database);
        _vocabulary = new VocabularyRepository(_database);
    }

    [Fact]
    public void Initialize_CreatesUsableDatabase()
    {
        Assert.True(_database.IsAvailable, _database.UnavailableReason);
        Assert.True(File.Exists(_databasePath));
    }

    [Fact]
    public void Add_ThenSearch_ReturnsRecordWithAllFields()
    {
        _history.Add("hello", "你好", "en", "zh-CN", "Bing");

        var records = _history.Search(null);

        var record = Assert.Single(records);
        Assert.Equal("hello", record.SourceText);
        Assert.Equal("你好", record.TranslatedText);
        Assert.Equal("en", record.SourceLanguage);
        Assert.Equal("zh-CN", record.TargetLanguage);
        Assert.Equal("Bing", record.Engine);
        Assert.True(record.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Search_IsOrderedNewestFirst()
    {
        _history.Add("first", "第一", "en", "zh-CN", "Bing");
        _history.Add("second", "第二", "en", "zh-CN", "Bing");

        var records = _history.Search(null);

        Assert.Equal("second", records[0].SourceText);
        Assert.Equal("first", records[1].SourceText);
    }

    [Fact]
    public void Search_MatchesSourceOrTranslation()
    {
        _history.Add("hello world", "你好世界", "en", "zh-CN", "Bing");
        _history.Add("goodbye", "再见", "en", "zh-CN", "Bing");

        Assert.Single(_history.Search("hello"));
        Assert.Single(_history.Search("再见"));
        Assert.Empty(_history.Search("不存在的内容"));
    }

    [Fact]
    public void Search_LikeWildcardsInKeywordAreEscaped()
    {
        _history.Add("100% sure", "百分之百确定", "en", "zh-CN", "Bing");
        _history.Add("plain", "普通", "en", "zh-CN", "Bing");

        // 未转义时 "%" 会匹配所有记录；转义后应只命中真正含 % 的那条
        var records = _history.Search("%");

        Assert.Single(records);
        Assert.Equal("100% sure", records[0].SourceText);
    }

    [Fact]
    public void Delete_RemovesOnlyTargetRecord()
    {
        _history.Add("a", "甲", "en", "zh-CN", "Bing");
        _history.Add("b", "乙", "en", "zh-CN", "Bing");
        var target = _history.Search("a").Single();

        _history.Delete(target.Id);

        Assert.Single(_history.Search(null));
        Assert.Equal("b", _history.Search(null)[0].SourceText);
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        _history.Add("a", "甲", "en", "zh-CN", "Bing");
        _history.Add("b", "乙", "en", "zh-CN", "Bing");

        _history.Clear();

        Assert.Empty(_history.Search(null));
        Assert.Equal(0, _history.Count());
    }

    [Fact]
    public void Add_ExceedingLimit_PrunesOldestRecords()
    {
        // 上限 5000：写入超量后只保留最近的，避免数据库无限增长
        for (var i = 0; i < HistoryRepository.MaxRecords + 5; i++)
        {
            _history.Add($"text-{i}", $"译文-{i}", "en", "zh-CN", "Bing");
        }

        Assert.Equal(HistoryRepository.MaxRecords, _history.Count());
        // 最旧的 5 条应已被清理
        Assert.Empty(_history.Search("text-0"));
        Assert.Single(_history.Search($"text-{HistoryRepository.MaxRecords + 4}"));
    }

    [Fact]
    public void Vocabulary_AddThenList_ReturnsEntry()
    {
        var added = _vocabulary.Add("hello", "你好", "en", "zh-CN");

        Assert.True(added);
        var entry = Assert.Single(_vocabulary.List());
        Assert.Equal("hello", entry.SourceText);
        Assert.Equal("zh-CN", entry.TargetLanguage);
    }

    [Fact]
    public void Vocabulary_SameSourceAndTarget_UpdatesInsteadOfDuplicating()
    {
        _vocabulary.Add("hello", "你好", "en", "zh-CN");

        var added = _vocabulary.Add("hello", "您好", "en", "zh-CN");

        Assert.False(added); // 表示更新而非新增
        var entry = Assert.Single(_vocabulary.List());
        Assert.Equal("您好", entry.TranslatedText);
    }

    [Fact]
    public void Vocabulary_DifferentTargetLanguages_AreSeparateEntries()
    {
        _vocabulary.Add("hello", "你好", "en", "zh-CN");
        _vocabulary.Add("hello", "ハロー", "en", "ja");

        Assert.Equal(2, _vocabulary.Count());
    }

    [Fact]
    public void Vocabulary_ContainsReflectsState()
    {
        Assert.False(_vocabulary.Contains("hello", "zh-CN"));

        _vocabulary.Add("hello", "你好", "en", "zh-CN");

        Assert.True(_vocabulary.Contains("hello", "zh-CN"));
        Assert.False(_vocabulary.Contains("hello", "ja"));
    }

    [Fact]
    public void Vocabulary_DeleteRemovesEntry()
    {
        _vocabulary.Add("hello", "你好", "en", "zh-CN");
        var entry = _vocabulary.List().Single();

        _vocabulary.Delete(entry.Id);

        Assert.Empty(_vocabulary.List());
    }

    [Fact]
    public void Vocabulary_ExportCsv_EscapesCommasQuotesAndNewlines()
    {
        _vocabulary.Add("a,b", "含\"引号\"", "en", "zh-CN");
        _vocabulary.Add("多行\n文本", "译文", "en", "zh-CN");

        var csv = _vocabulary.ExportCsv();

        Assert.Contains("\"a,b\"", csv);              // 含逗号 → 加引号
        Assert.Contains("\"含\"\"引号\"\"\"", csv);    // 引号翻倍
        Assert.Contains("\"多行\n文本\"", csv);        // 含换行 → 加引号
        Assert.StartsWith("原文,译文,源语言,目标语言,收藏时间", csv);
    }

    [Fact]
    public void Vocabulary_ExportTsv_ReplacesTabsAndNewlines()
    {
        _vocabulary.Add("tab\there", "换行\n译文", "en", "zh-CN");

        var tsv = _vocabulary.ExportTsv();

        // 一条记录必须恰好占一行（字段内的制表符与换行都已替换为空格），否则 Anki 导入会错列
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines).TrimEnd('\r');
        var fields = line.Split('\t');
        Assert.Equal(3, fields.Length); // 正面 / 背面 / 标签
        Assert.Equal("tab here", fields[0]);
        Assert.Equal("换行 译文", fields[1]);
        Assert.Equal("en-zh-CN", fields[2]);
    }

    [Fact]
    public void Repository_WhenDatabaseUnavailable_ReturnsEmptyWithoutThrowing()
    {
        // 指向非法路径（例如目录名冲突）以模拟数据库不可用
        var broken = new HistoryDatabase(Path.Combine(Path.GetTempPath(), "ta-test-dir-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(broken.DatabasePath); // 让 Sqlite 无法把它当作数据库文件
        broken.Initialize();
        var repository = new HistoryRepository(broken);
        var vocabulary = new VocabularyRepository(broken);

        Assert.False(broken.IsAvailable);
        Assert.Empty(repository.Search(null));
        Assert.Equal(0, repository.Count());
        Assert.Empty(vocabulary.List());
        repository.Add("a", "b", "en", "zh-CN", "Bing"); // 不应抛异常
        repository.Clear();

        Directory.Delete(broken.DatabasePath, recursive: true);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch
            {
                // 测试清理失败不影响结论
            }
        }
    }
}
