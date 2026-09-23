using System.Text;

namespace TranslationApp.Core.Translation;

/// <summary>术语表文件解析结果；Items 已过滤完全重复项，Warnings 供导入前预览。</summary>
public sealed record GlossaryImportPreview(
    IReadOnlyList<GlossaryItem> Items,
    IReadOnlyList<string> Warnings,
    int DuplicateCount,
    int SkippedCount);

/// <summary>
/// 术语表 CSV/TSV 交换格式。使用标准双引号转义，支持字段内逗号、制表符、双引号与换行；
/// 首行可省略，省略时按「源词, 译法, 启用, 源语言, 目标语言, 匹配方式, 备注」读取。
/// </summary>
public static class GlossaryFileCodec
{
    private static readonly string[] Headers =
        ["源词", "译法", "启用", "源语言", "目标语言", "匹配方式", "备注"];

    public static string ExportCsv(IReadOnlyList<GlossaryItem> items) => Export(items, ',');

    public static string ExportTsv(IReadOnlyList<GlossaryItem> items) => Export(items, '\t');

    public static GlossaryImportPreview ParseCsv(
        string text, IReadOnlyList<GlossaryItem>? existing = null) => Parse(text, ',', existing);

    public static GlossaryImportPreview ParseTsv(
        string text, IReadOnlyList<GlossaryItem>? existing = null) => Parse(text, '\t', existing);

    public static string Export(IReadOnlyList<GlossaryItem> items, char delimiter)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(delimiter, Headers.Select(value => Escape(value, delimiter))));
        foreach (var item in items.Take(GlossaryReplacer.MaxItems))
        {
            builder.AppendLine(string.Join(delimiter, new[]
            {
                Escape(item.Source, delimiter),
                Escape(item.Target, delimiter),
                Escape(item.Enabled ? "true" : "false", delimiter),
                Escape(item.SourceLanguage, delimiter),
                Escape(item.TargetLanguage, delimiter),
                Escape(GlossaryMatchModes.Normalize(item.MatchMode), delimiter),
                Escape(item.Note, delimiter),
            }));
        }

        return builder.ToString();
    }

    public static GlossaryImportPreview Parse(
        string? text,
        char delimiter,
        IReadOnlyList<GlossaryItem>? existing = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new GlossaryImportPreview([], [], 0, 0);
        }

        var rows = ParseRows(text, delimiter);
        if (rows.Count == 0)
        {
            return new GlossaryImportPreview([], [], 0, 0);
        }

        var warnings = new List<string>();
        var hasHeader = rows[0].Any(cell => CanonicalHeader(cell) is not null);
        var map = hasHeader ? BuildHeaderMap(rows[0]) : ColumnMap.Positional;
        var start = hasHeader ? 1 : 0;

        var parsed = new List<GlossaryItem>();
        var duplicateCount = 0;
        var skippedCount = 0;
        for (var rowIndex = start; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var source = Read(row, map.Source).Trim();
            var target = Read(row, map.Target).Trim();
            if (source.Length == 0 || target.Length == 0)
            {
                skippedCount++;
                AddWarning(warnings, $"第 {rowIndex + 1} 行缺少源词或译法，已跳过");
                continue;
            }

            var enabled = true;
            var enabledText = Read(row, map.Enabled).Trim();
            if (enabledText.Length > 0 && !TryParseEnabled(enabledText, out enabled))
            {
                AddWarning(warnings, $"第 {rowIndex + 1} 行「启用」值无法识别，按启用处理");
                enabled = true;
            }

            var mode = ParseMatchMode(Read(row, map.MatchMode), warnings, rowIndex + 1);
            parsed.Add(new GlossaryItem(
                source,
                target,
                enabled,
                ParseLanguage(Read(row, map.SourceLanguage), source: true),
                ParseLanguage(Read(row, map.TargetLanguage), source: false),
                mode,
                Read(row, map.Note).Trim()));
        }

        var comparison = existing is null ? new List<GlossaryItem>() : [.. existing];
        var accepted = new List<GlossaryItem>();
        foreach (var item in parsed)
        {
            if (comparison.Any(candidate => IsExactDuplicate(item, candidate)))
            {
                duplicateCount++;
                continue;
            }

            var conflict = DescribeConflict(item, comparison);
            if (conflict is not null)
            {
                AddWarning(warnings, conflict);
            }

            if (accepted.Count >= GlossaryReplacer.MaxItems)
            {
                skippedCount++;
                continue;
            }

            accepted.Add(item);
            comparison.Add(item);
        }

        if (skippedCount > 0)
        {
            AddWarning(warnings, $"共有 {skippedCount} 条未导入（格式不完整或超过 {GlossaryReplacer.MaxItems} 条上限）");
        }

        if (duplicateCount > 0)
        {
            AddWarning(warnings, $"自动跳过 {duplicateCount} 条完全重复词条");
        }

        return new GlossaryImportPreview(accepted, warnings, duplicateCount, skippedCount);
    }

    private static ColumnMap BuildHeaderMap(IReadOnlyList<string> header)
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var canonical = CanonicalHeader(header[i]);
            if (canonical is not null)
            {
                indices.TryAdd(canonical, i);
            }
        }

        if (!indices.TryGetValue("source", out var source) ||
            !indices.TryGetValue("target", out var target))
        {
            throw new InvalidDataException("术语表文件缺少「源词」或「译法」列");
        }

        return new ColumnMap(
            source,
            target,
            GetIndex(indices, "enabled"),
            GetIndex(indices, "sourceLanguage"),
            GetIndex(indices, "targetLanguage"),
            GetIndex(indices, "matchMode"),
            GetIndex(indices, "note"));
    }

    private static int GetIndex(IReadOnlyDictionary<string, int> indices, string key) =>
        indices.TryGetValue(key, out var index) ? index : -1;

    private static string? CanonicalHeader(string? value)
    {
        var normalized = (value ?? "").Trim().TrimStart('\uFEFF')
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized switch
        {
            "source" or "源词" or "原文" => "source",
            "target" or "译法" or "译文" => "target",
            "enabled" or "启用" or "是否启用" => "enabled",
            "sourcelanguage" or "源语言" => "sourceLanguage",
            "targetlanguage" or "目标语言" => "targetLanguage",
            "matchmode" or "匹配方式" or "匹配模式" => "matchMode",
            "note" or "备注" => "note",
            _ => null,
        };
    }

    private static string Read(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : "";

    private static bool TryParseEnabled(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "y" or "启用" or "是":
                enabled = true;
                return true;
            case "false" or "0" or "no" or "n" or "停用" or "禁用" or "否":
                enabled = false;
                return true;
            default:
                enabled = true;
                return false;
        }
    }

    private static string ParseMatchMode(string value, ICollection<string> warnings, int rowNumber)
    {
        var normalized = value.Trim();
        var mode = normalized.ToLowerInvariant() switch
        {
            "" or "word" or "整词" or "词边界" or "普通词" => GlossaryMatchModes.Word,
            "contains" or "包含" or "子串" => GlossaryMatchModes.Contains,
            "casesensitive" or "区分大小写" or "大小写敏感" => GlossaryMatchModes.CaseSensitive,
            "regex" or "regexp" or "正则" or "正则表达式" => GlossaryMatchModes.Regex,
            _ => "",
        };
        if (mode.Length > 0)
        {
            return mode;
        }

        AddWarning(warnings, $"第 {rowNumber} 行匹配方式「{normalized}」无法识别，按整词处理");
        return GlossaryMatchModes.Word;
    }

    private static string ParseLanguage(string value, bool source)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed == "*" || trimmed is "全部" or "所有" or "任意")
        {
            return "*";
        }

        var options = source ? TranslationLanguages.SourceOptions : TranslationLanguages.TargetOptions;
        var match = options.FirstOrDefault(option =>
            string.Equals(option.Code, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Display, trimmed, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? trimmed;
    }

    private static bool IsExactDuplicate(GlossaryItem left, GlossaryItem right) =>
        string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Target, right.Target, StringComparison.Ordinal) &&
        string.Equals(left.SourceLanguage, right.SourceLanguage, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.TargetLanguage, right.TargetLanguage, StringComparison.OrdinalIgnoreCase);

    private static string? DescribeConflict(GlossaryItem item, IReadOnlyList<GlossaryItem> existing)
    {
        foreach (var candidate in existing)
        {
            if (!string.Equals(item.SourceLanguage, candidate.SourceLanguage, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(item.TargetLanguage, candidate.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(item.Source, candidate.Target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Target, candidate.Source, StringComparison.OrdinalIgnoreCase))
            {
                return $"反向冲突：{item.Source} → {item.Target} 与已有 {candidate.Source} → {candidate.Target} 方向相反";
            }

            if (!string.Equals(item.Source, candidate.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Target, candidate.Target, StringComparison.Ordinal) &&
                (item.Source.Contains(candidate.Source, StringComparison.OrdinalIgnoreCase) ||
                 candidate.Source.Contains(item.Source, StringComparison.OrdinalIgnoreCase)))
            {
                return $"包含关系：{item.Source} 与已有 {candidate.Source} 共用同一译法，可能产生重复命中";
            }
        }

        return null;
    }

    private static void AddWarning(ICollection<string> warnings, string warning)
    {
        if (warnings.Count < 30)
        {
            warnings.Add(warning);
        }
    }

    private static string Escape(string value, char delimiter)
    {
        var text = value ?? "";
        var needsQuote = text.Contains(delimiter) || text.Contains('"') ||
                         text.Contains('\r') || text.Contains('\n');
        return needsQuote ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private static List<List<string>> ParseRows(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("术语表文件存在未闭合的双引号");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(value => value.Length > 0))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private sealed record ColumnMap(
        int Source,
        int Target,
        int Enabled,
        int SourceLanguage,
        int TargetLanguage,
        int MatchMode,
        int Note)
    {
        public static ColumnMap Positional { get; } = new(0, 1, 2, 3, 4, 5, 6);
    }
}
