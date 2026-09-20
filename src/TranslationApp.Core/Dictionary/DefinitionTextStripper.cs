using System.Globalization;
using System.Text;

namespace TranslationApp.Core.Dictionary;

/// <summary>
/// 词典定义清洗（FR-049 最小可行）：mdx 的词条正文常见两种富文本 —— 内联 RTF 与 HTML。
/// 这里只做到「读得懂的纯文本」：去标签/控制字、解常见实体、块级元素转换行、压缩多余空行。
/// 不渲染图片词条（spec §10 明确不做），也不追求严格解析器——脏输入一律按「尽量留文本」处理。
/// </summary>
public static class DefinitionTextStripper
{
    /// <summary>RTF → 纯文本：跳过控制字与格式组，<c>\par</c> 与组结束算换行，保留转义字符。</summary>
    public static string StripRtf(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf) || !rtf.Contains('\\'))
        {
            return rtf ?? string.Empty;
        }

        var sb = new StringBuilder(rtf.Length / 2 + 16);
        var skipDepth = 0; // destination 选择组（\*\...）整体丢弃
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    i++;
                    break;
                case '}':
                    i++;
                    if (skipDepth > 0)
                    {
                        skipDepth--;
                    }
                    else
                    {
                        NewLine(sb);
                    }

                    break;
                case '\\' when i + 1 < rtf.Length:
                    i = ConsumeControl(rtf, i, sb, ref skipDepth);
                    break;
                default:
                    if (skipDepth == 0 && c != '\r' && c != '\n')
                    {
                        sb.Append(c);
                    }

                    i++;
                    break;
            }
        }

        return Normalize(sb);
    }

    /// <summary>HTML → 纯文本：去标签（script/style 连内容一起去）、解实体、块级标签转换行。</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains('<'))
        {
            return html ?? string.Empty;
        }

        var sb = new StringBuilder(html.Length / 2 + 16);
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var end = html.IndexOf('>', i);
                if (end < 0)
                {
                    break; // 畸形输入：后面的文本没有可靠的结构可言，保留已取到的部分
                }

                var raw = html[(i + 1)..end];
                var tag = raw.TrimStart();
                if (tag.StartsWith("script", StringComparison.OrdinalIgnoreCase)
                    || tag.StartsWith("style", StringComparison.OrdinalIgnoreCase))
                {
                    var name = tag[..6];
                    var close = raw.StartsWith('/')
                        ? -1
                        : IndexOfIgnoreCase(html, "</" + name, end);
                    if (close >= 0)
                    {
                        var closeEnd = html.IndexOf('>', close);
                        i = closeEnd < 0 ? html.Length : closeEnd + 1;
                        continue;
                    }

                    i = end + 1; // 找不到配对（或本来就是闭标签）→ 只丢这个标签本身
                    continue;
                }

                if (IsBlockTag(tag))
                {
                    NewLine(sb);
                }

                i = end + 1;
                continue;
            }

            if (html[i] == '&')
            {
                i += DecodeEntity(html, i, sb);
                continue;
            }

            sb.Append(html[i]);
            i++;
        }

        return Normalize(sb);
    }

    /// <summary>按内容特征自动选择清洗方式（RTF 以 <c>{\rtf</c> 开头；含标签的按 HTML 处理）。</summary>
    public static string Strip(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{\\rtf", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\\fonttbl", StringComparison.OrdinalIgnoreCase))
        {
            return StripRtf(trimmed);
        }

        return trimmed.Contains('<') ? StripHtml(trimmed) : Normalize(new StringBuilder(trimmed));
    }

    /// <summary>处理一个反斜杠控制字（<c>\\</c>、<c>\'</c>、<c>\par</c>、<c>\uNNNN</c>、<c>\*\dest</c>…），返回新的下标。</summary>
    private static int ConsumeControl(string rtf, int i, StringBuilder sb, ref int skipDepth)
    {
        var next = rtf[i + 1];
        if (next == '\\' || next == '{' || next == '}')
        {
            if (skipDepth == 0)
            {
                sb.Append(next);
            }

            return i + 2;
        }

        if (next == '*')
        {
            skipDepth++; // 整个 \*\... 组视为二进制/图片数据丢弃
            return i + 2;
        }

        if (!char.IsLetter(next))
        {
            switch (next)
            {
                // \\'xx 只在 ASCII 段还原（RTF 里的中文由 \u 控制字承载）
                case '\'' when i + 3 < rtf.Length && TryHexByte(rtf.AsSpan(i + 2, 2), out var byteValue):
                    if (skipDepth == 0)
                    {
                        sb.Append(byteValue < 0x80 ? (char)byteValue : ' ');
                    }

                    return i + 4;
                // RTF 的三个转义字母控制字；其余单字符控制符号（\; \~ \- \* 等）不是文字，一律跳过
                case 'a' when skipDepth == 0:
                    sb.Append('\a');
                    return i + 3;
                case 'A' when skipDepth == 0:
                    sb.Append('\u2011');
                    return i + 3;
                case 'N' when skipDepth == 0:
                    sb.Append('\u2011');
                    return i + 3;
                default:
                    return i + 2;
            }
        }

        var wordStart = i + 1;
        var wordEnd = wordStart;
        while (wordEnd < rtf.Length && char.IsLetter(rtf[wordEnd]))
        {
            wordEnd++;
        }

        var numberStart = wordEnd;
        var numberEnd = numberStart;
        if (numberEnd < rtf.Length && (rtf[numberEnd] == '-' || char.IsDigit(rtf[numberEnd])))
        {
            numberEnd++;
            while (numberEnd < rtf.Length && char.IsDigit(rtf[numberEnd]))
            {
                numberEnd++;
            }
        }

        if (numberEnd < rtf.Length && rtf[numberEnd] == ' ')
        {
            numberEnd++; // 分隔用空格属于控制字，不进正文
        }

        if (skipDepth == 0)
        {
            var word = rtf[wordStart..numberStart];
            switch (word)
            {
                case "par" or "line" or "tab":
                    NewLine(sb);
                    break;
                // 只有 \u 的数值是正文码点；\rtf1、\ansicpg936 这类格式控制字的数字是参数，不是文字
                case "u"
                    when int.TryParse(rtf.AsSpan(numberStart, numberEnd - numberStart), System.Globalization.NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out var codePoint)
                         && codePoint > 0:
                    AppendCodePoint(sb, codePoint);
                    break;
            }
        }

        return numberEnd;
    }

    private static int DecodeEntity(string html, int i, StringBuilder sb)
    {
        var end = html.IndexOf(';', i, Math.Min(12, html.Length - i));
        if (end < 0)
        {
            sb.Append(html[i]);
            return 1;
        }

        var body = html[(i + 1)..end];
        var consumed = end - i + 1;
        switch (body)
        {
            case "amp":
                sb.Append('&');
                return consumed;
            case "lt":
                sb.Append('<');
                return consumed;
            case "gt":
                sb.Append('>');
                return consumed;
            case "quot":
                sb.Append('"');
                return consumed;
            case "apos":
                sb.Append('\'');
                return consumed;
            case "nbsp":
                sb.Append(' ');
                return consumed;
        }

        if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(body[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            AppendCodePoint(sb, hex);
            return consumed;
        }

        if (body.StartsWith('#') && int.TryParse(body[1..], out var dec))
        {
            AppendCodePoint(sb, dec);
            return consumed;
        }

        sb.Append(html[i]);
        return 1;
    }

    private static void AppendCodePoint(StringBuilder sb, int value)
    {
        try
        {
            sb.Append(char.ConvertFromUtf32(value));
        }
        catch (ArgumentOutOfRangeException)
        {
            // 非法码点：宁可不输出，也不给出乱码
        }
    }

    private static int IndexOfIgnoreCase(string haystack, string needle, int from) =>
        from >= haystack.Length ? -1 : haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockTag(string tag)
    {
        var name = new string(tag.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return name is "p" or "br" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "dt" or "dd";
    }

    private static void NewLine(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }

    private static bool TryHexByte(ReadOnlySpan<char> span, out byte value)
    {
        value = 0;
        if (span.Length < 2 || !IsHex(span[0]) || !IsHex(span[1]))
        {
            return false;
        }

        value = (byte)((HexValue(span[0]) << 4) | HexValue(span[1]));
        return true;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };

    /// <summary>行尾空白清理 + 连续空行压成一个。</summary>
    private static string Normalize(StringBuilder sb)
    {
        var text = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n').Select(line => line.TrimEnd()).ToList();
        var result = new StringBuilder();
        var blankRuns = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blankRuns++;
                if (blankRuns > 1)
                {
                    continue;
                }
            }
            else
            {
                blankRuns = 0;
            }

            if (result.Length > 0)
            {
                result.Append('\n');
            }

            result.Append(line);
        }

        return result.ToString().Trim();
    }
}
