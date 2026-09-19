namespace TranslationApp.Core.Capture;

/// <summary>
/// OCR 行文本合并（13.2.2）：<c>OcrResult.Text</c> 的语义是「按行以 \n 连接」，
/// 这里显式复刻该语义，使合并规则可被单元测试固定下来。
/// </summary>
public static class OcrTextComposer
{
    /// <summary>逐行拼接：去掉空行与行首尾空白，行间用 \n（不额外插入空格，避免中文被拆成空格串）。</summary>
    public static string Compose(IEnumerable<string?> lines)
    {
        if (lines is null)
        {
            return "";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }
}
