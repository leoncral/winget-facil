using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Asa.Services;

public sealed record WingetTableRow(
    string Name,
    string Id,
    string Version,
    string AvailableVersion,
    string Source);

public static partial class WingetTableParser
{
    public static IReadOnlyList<WingetTableRow> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        HeaderLayout? layout = null;
        var headerLineIndex = -1;

        for (var index = 0; index < lines.Length; index++)
        {
            var candidate = Clean(lines[index]);
            var possibleLayout = TryReadHeader(candidate);
            if (possibleLayout is not null)
            {
                layout = possibleLayout;
                headerLineIndex = index;
                break;
            }
        }

        if (layout is null)
        {
            return [];
        }

        var rows = new List<WingetTableRow>();

        for (var index = headerLineIndex + 1; index < lines.Length; index++)
        {
            var line = Clean(lines[index]);
            if (string.IsNullOrWhiteSpace(line) || IsSeparator(line))
            {
                continue;
            }

            var name = ReadCell(line, layout.NameStart, layout.IdStart);
            var id = ReadCell(line, layout.IdStart, layout.VersionStart);

            var versionEnd = layout.AvailableStart
                ?? layout.MatchStart
                ?? layout.SourceStart
                ?? int.MaxValue;
            var version = ReadCell(line, layout.VersionStart, versionEnd);

            var available = string.Empty;
            if (layout.AvailableStart is int availableStart)
            {
                var availableEnd = layout.MatchStart
                    ?? layout.SourceStart
                    ?? int.MaxValue;
                available = ReadCell(line, availableStart, availableEnd);
            }

            var source = layout.SourceStart is int sourceStart
                ? ReadCell(line, sourceStart, int.MaxValue)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(id) ||
                name.StartsWith('<') ||
                id.Contains(' '))
            {
                continue;
            }

            rows.Add(new WingetTableRow(name.Trim(), id.Trim(), version.Trim(), available.Trim(), source.Trim()));
        }

        return rows;
    }

    private static HeaderLayout? TryReadHeader(string line)
    {
        var columns = ReadHeaderColumns(line);
        if (columns.Count < 4)
        {
            return null;
        }

        // Column order is always Name, Id, Version, then either Available/Match and Source.
        // The labels themselves are localized, so only the stable Id and Version labels are checked.
        if (!IsLabel(columns[1].Text, "id") ||
            !IsVersionLabel(columns[2].Text) ||
            columns[0].Start >= columns[1].Start ||
            columns[1].Start >= columns[2].Start)
        {
            return null;
        }

        int? availableStart = null;
        int? matchStart = null;
        int? sourceStart = null;

        // Labels may be separated by a single space when a column is narrower than its
        // header (for example "Disponível Origem"), so inspect every header token.
        for (var index = 3; index < columns.Count; index++)
        {
            var label = NormalizeLabel(columns[index].Text);

            if (IsSourceLabel(label))
            {
                sourceStart = columns[index].Start;
                break;
            }

            if (availableStart is null && matchStart is null)
            {
                if (IsAvailableLabel(label))
                {
                    availableStart = columns[index].Start;
                }
                else
                {
                    matchStart = columns[index].Start;
                }
            }
        }

        return new HeaderLayout(
            columns[0].Start,
            columns[1].Start,
            columns[2].Start,
            availableStart,
            matchStart,
            sourceStart);
    }

    private static IReadOnlyList<HeaderColumn> ReadHeaderColumns(string line)
    {
        // Split on every whitespace run: column gaps shrink to a single space when a
        // value column is narrower than its label, and two labels can end up adjacent.
        var columns = new List<HeaderColumn>();
        var index = 0;

        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            if (index >= line.Length)
            {
                break;
            }

            var start = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            columns.Add(new HeaderColumn(start, line[start..index]));
        }

        return columns;
    }

    private static bool IsLabel(string value, string expected)
    {
        return string.Equals(NormalizeLabel(value), expected, StringComparison.Ordinal);
    }

    private static bool IsVersionLabel(string value)
    {
        var normalized = NormalizeLabel(value);
        return normalized.StartsWith("vers", StringComparison.Ordinal) ||
               normalized.StartsWith("wers", StringComparison.Ordinal) ||
               normalized.StartsWith("verz", StringComparison.Ordinal);
    }

    private static bool IsAvailableLabel(string normalized)
    {
        return normalized.Contains("available", StringComparison.Ordinal) ||
               normalized.Contains("dispon", StringComparison.Ordinal) ||
               normalized.Contains("verfug", StringComparison.Ordinal) ||
               normalized.Contains("beschik", StringComparison.Ordinal) ||
               normalized.Contains("dostep", StringComparison.Ordinal);
    }

    private static bool IsSourceLabel(string normalized)
    {
        return normalized.Contains("source", StringComparison.Ordinal) ||
               normalized.Contains("origem", StringComparison.Ordinal) ||
               normalized.Contains("origen", StringComparison.Ordinal) ||
               normalized.Contains("origine", StringComparison.Ordinal) ||
               normalized.Contains("quelle", StringComparison.Ordinal) ||
               normalized.Contains("bron", StringComparison.Ordinal) ||
               normalized.Contains("fonte", StringComparison.Ordinal);
    }

    private static string NormalizeLabel(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Trim();
    }

    private static string ReadCell(string line, int start, int end)
    {
        if (start == int.MaxValue || start >= line.Length)
        {
            return string.Empty;
        }

        var safeEnd = end == int.MaxValue ? line.Length : Math.Min(end, line.Length);
        var length = safeEnd - start;
        return length <= 0 ? string.Empty : line.Substring(start, length).Trim();
    }

    private static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 2 && trimmed.All(character => character is '-' or '=' or ' ');
    }

    private static string Clean(string value)
    {
        var withoutAnsi = AnsiEscapeRegex().Replace(value, string.Empty);
        return withoutAnsi.Replace("\b", string.Empty, StringComparison.Ordinal).TrimEnd();
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapeRegex();

    private sealed record HeaderColumn(int Start, string Text);

    private sealed record HeaderLayout(
        int NameStart,
        int IdStart,
        int VersionStart,
        int? AvailableStart,
        int? MatchStart,
        int? SourceStart);
}
