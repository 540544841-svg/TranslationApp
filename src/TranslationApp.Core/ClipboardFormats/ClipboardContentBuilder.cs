namespace TranslationApp.Core.ClipboardFormats;

/// <summary>复制策略（FR-044 / spec §2）。</summary>
public enum ClipboardCopyMode
{
    TranslationOnly,
    SourceOnly,
    Both,
}

/// <summary>按复制策略拼剪贴板文本（纯函数，供小窗与钉图工具条复用）。Both 用空行分隔，粘贴后可整块删改。</summary>
public static class ClipboardContentBuilder
{
    public static string Build(string source, string translated, ClipboardCopyMode mode) => mode switch
    {
        ClipboardCopyMode.SourceOnly => source,
        ClipboardCopyMode.Both => source + "\n\n" + translated,
        _ => translated,
    };
}
