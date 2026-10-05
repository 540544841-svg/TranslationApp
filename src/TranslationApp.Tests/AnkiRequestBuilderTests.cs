using System.Text.Json;
using TranslationApp.Core.Anki;
using Xunit;

namespace TranslationApp.Tests;

/// <summary>FR-035：AnkiConnect JSON-RPC 请求构造（纯函数，零网络）。spec §1.1。</summary>
public class AnkiRequestBuilderTests
{
    private static AnkiNoteRequest Note(string front = "apple") => new(
        "生词本", "基本", "正面", front, "背面", "苹果", ["译印", "en-zh-CN"]);

    [Fact]
    public void VersionRequest_CarriesActionAndApiVersion()
    {
        using var doc = JsonDocument.Parse(AnkiRequestBuilder.BuildVersionRequest());
        Assert.Equal("version", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(6, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void AddNote_MapsDeckModelFieldsAndTags()
    {
        using var doc = JsonDocument.Parse(AnkiRequestBuilder.BuildAddNoteRequest(Note("a\"b\nc")));
        var p = doc.RootElement.GetProperty("params");
        Assert.Equal("addNote", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(6, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("生词本", p.GetProperty("deckName").GetString());
        Assert.Equal("基本", p.GetProperty("modelName").GetString());
        // 引号/换行必须经 JSON 转义无损往返
        Assert.Equal("a\"b\nc", p.GetProperty("fields").GetProperty("正面").GetString());
        Assert.Equal("苹果", p.GetProperty("fields").GetProperty("背面").GetString());
        Assert.Equal(
            ["译印", "en-zh-CN"],
            p.GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public void AddNotes_PreservesOrderAndCount()
    {
        var notes = new[] { Note("first"), Note("second") };
        using var doc = JsonDocument.Parse(AnkiRequestBuilder.BuildAddNotesRequest(notes));
        var items = doc.RootElement.GetProperty("params").GetProperty("notes").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("first", items[0].GetProperty("fields").GetProperty("正面").GetString());
        Assert.Equal("second", items[1].GetProperty("fields").GetProperty("正面").GetString());
    }

    [Fact]
    public void FromVocabulary_BuildsLanguagePairTag()
    {
        var note = AnkiRequestBuilder.FromVocabulary(
            "生词本", "基本", "正面", "背面", "apple", "苹果", "en", "zh-CN");
        Assert.Equal("apple", note.Front);
        Assert.Equal("苹果", note.Back);
        Assert.Equal(["译印", "en-zh-CN"], note.Tags);
    }

    [Fact]
    public void FromVocabulary_SkipsPairTag_WhenLanguageMissing()
    {
        var note = AnkiRequestBuilder.FromVocabulary(
            "d", "m", "f", "b", "apple", "苹果", "", "zh-CN");
        Assert.Equal(["译印"], note.Tags);
    }

    [Fact]
    public void Batch_SplitsBy50()
    {
        var batches = AnkiRequestBuilder.Batch(Enumerable.Repeat(Note(), 120).ToArray());
        Assert.Equal([50, 50, 20], batches.Select(b => b.Count).ToArray());
        Assert.Equal(120, batches.Sum(b => b.Count));
    }

    [Fact]
    public void Batch_EmptyInput_ProducesNoBatches()
    {
        Assert.Empty(AnkiRequestBuilder.Batch([]));
    }
}
