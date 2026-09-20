namespace TranslationApp.Core.Capture;

/// <summary>一张图的批量识别结果（Error 只存异常类型名/固定短语，不外泄消息文本）。</summary>
public sealed record BatchOcrItem(string Path, bool Ok, string? Text, string? Error)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// 批量 OCR 编排（FR-047 / spec §5）：逐张调用注入的识别委托，单张失败不中断，
/// 产出可导出 Markdown 的结果列表。识别器由 App 层包装现有 OcrService 注入——
/// 本类零 IO、零 WPF，可单测。PDF 不在支持面（无第三方解析依赖，UI 文案写明「请先转图片」）。
/// </summary>
public static class BatchOcrRunner
{
    /// <summary>支持的图片扩展名（小写比较）。</summary>
    public static IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
        };

    public static bool IsSupportedImage(string path) =>
        SupportedExtensions.Contains(System.IO.Path.GetExtension(path));

    public static async Task<IReadOnlyList<BatchOcrItem>> RunAsync(
        IReadOnlyList<string> files,
        Func<string, Task<string?>> recognize,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BatchOcrItem>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[i];
            try
            {
                var text = await recognize(path);
                results.Add(string.IsNullOrWhiteSpace(text)
                    ? new BatchOcrItem(path, false, null, "未识别到文字")
                    : new BatchOcrItem(path, true, text.Trim(), null));
            }
            catch (OperationCanceledException)
            {
                throw; // 取消是用户意图，不算单张失败
            }
            catch (Exception ex)
            {
                results.Add(new BatchOcrItem(path, false, null, ex.GetType().Name));
            }

            progress?.Report((i + 1, files.Count));
        }

        return results;
    }

    /// <summary>导出 Markdown：每图一节（## 文件名），失败节写引用行原因。</summary>
    public static string BuildMarkdown(IReadOnlyList<BatchOcrItem> items)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            sb.Append("## ").Append(item.FileName).Append("\n\n");
            if (item.Ok)
            {
                sb.Append(item.Text).Append('\n');
            }
            else
            {
                sb.Append("> 识别失败：").Append(item.Error ?? "未知原因").Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }
}
