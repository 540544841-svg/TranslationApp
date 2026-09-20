using System.Text.Json;

namespace TranslationApp.Core.Anki;

/// <summary>一条待推送笔记（正面=原文、背面=译文；标签含「速译」与语言对）。FR-035。</summary>
public sealed record AnkiNoteRequest(
    string Deck,
    string Model,
    string FrontField,
    string Front,
    string BackField,
    string Back,
    IReadOnlyList<string> Tags);

/// <summary>连通性探测结果。Reason 只允许安全摘要（异常类型 / HTTP 状态），绝不携带用户文本。</summary>
public sealed record AnkiProbeResult(bool Ok, int? Version, string? Reason);

/// <summary>批量推送统计：Skipped = Anki 判为重复/无效（result 项为 null）；Failed = 传输失败或整批被拒。</summary>
public sealed record AnkiPushResult(int Added, int Skipped, int Failed, string? Reason);

/// <summary>
/// AnkiConnect JSON-RPC 请求构造（FR-035 / spec §1.1）：纯函数、零 IO。
/// 协议体形态：<c>{"action":…,"version":6,"params":{…}}</c>。
/// </summary>
public static class AnkiRequestBuilder
{
    /// <summary>AnkiConnect API 版本（addNotes 等基础动作自 v2 起稳定，取现行通用值 6）。</summary>
    public const int ApiVersion = 6;

    /// <summary>单请求最大笔记数（spec §1.3：批量分批，每批 50）。</summary>
    public const int BatchSize = 50;

    public static string BuildVersionRequest() =>
        JsonSerializer.Serialize(new { action = "version", version = ApiVersion });

    public static string BuildAddNoteRequest(AnkiNoteRequest note) =>
        JsonSerializer.Serialize(new { action = "addNote", version = ApiVersion, @params = NoteObject(note) });

    public static string BuildAddNotesRequest(IReadOnlyList<AnkiNoteRequest> notes) =>
        JsonSerializer.Serialize(new
        {
            action = "addNotes",
            version = ApiVersion,
            @params = new { notes = notes.Select(NoteObject).ToArray() },
        });

    /// <summary>把一条生词快照成笔记：标签固定「速译」+ 语言对（语言缺失时只挂「速译」）。</summary>
    public static AnkiNoteRequest FromVocabulary(
        string deck, string model, string frontField, string backField,
        string source, string translated, string sourceLanguage, string targetLanguage)
    {
        var tags = new List<string> { "速译" };
        if (!string.IsNullOrWhiteSpace(sourceLanguage) && !string.IsNullOrWhiteSpace(targetLanguage))
        {
            tags.Add($"{sourceLanguage}-{targetLanguage}");
        }

        return new AnkiNoteRequest(deck, model, frontField, source, backField, translated, tags);
    }

    /// <summary>按 <see cref="BatchSize"/> 切分（空输入返回零批）。</summary>
    public static IReadOnlyList<IReadOnlyList<AnkiNoteRequest>> Batch(IReadOnlyList<AnkiNoteRequest> notes)
    {
        var batches = new List<IReadOnlyList<AnkiNoteRequest>>();
        for (var i = 0; i < notes.Count; i += BatchSize)
        {
            batches.Add(notes.Skip(i).Take(BatchSize).ToArray());
        }

        return batches;
    }

    private static object NoteObject(AnkiNoteRequest n) => new
    {
        deckName = n.Deck,
        modelName = n.Model,
        fields = new Dictionary<string, string> { [n.FrontField] = n.Front, [n.BackField] = n.Back },
        tags = n.Tags,
    };
}
