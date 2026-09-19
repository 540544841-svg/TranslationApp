namespace TranslationApp.Core.Translation;

/// <summary>
/// 长文本分块（FR-006 / 13.1.1）：各引擎单次请求都有长度上限，超出必须切块后逐块翻译再拼接。
/// 从 BingTranslator 提取为共享工具，四家官方引擎复用同一套切分规则，
/// 保证「同一段文本无论走哪个引擎，切块位置一致」，便于横向对比译文。
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// 按段落/句末标点切块，避免从词中间截断（截断点须超过块长 1/4，否则宁可按长度硬切，
    /// 防止连续短句导致块过小、请求数暴增）。
    /// </summary>
    public static IEnumerable<string> Split(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxLength, text.Length - start);
            if (start + length < text.Length)
            {
                var window = text.Substring(start, length);
                var cut = window.LastIndexOfAny(['\n', '。', '！', '？', '.', '!', '?', '；', ';']);
                if (cut > maxLength / 4)
                {
                    length = cut + 1;
                }
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }
}
