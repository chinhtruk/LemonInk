using System.Text.RegularExpressions;

namespace ZenRead.Services.Summarization;

public sealed record SourceChapterReference(int Number, string Title);

public static partial class SourceChapterOutlineExtractor
{
    private const int MinimumReliableChapterCount = 3;
    private const int MaximumReliableChapterNumber = 120;

    public static IReadOnlyList<SourceChapterReference> Extract(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Array.Empty<SourceChapterReference>();
        }

        var lines = source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var chapters = new Dictionary<int, SourceChapterReference>();

        for (var index = 0; index < lines.Length; index++)
        {
            var heading = NormalizeLine(lines[index]);
            var match = ChapterHeadingRegex().Match(heading);
            if (!match.Success ||
                !int.TryParse(match.Groups["number"].Value, out var number) ||
                number <= 0 ||
                number > MaximumReliableChapterNumber ||
                chapters.ContainsKey(number))
            {
                continue;
            }

            var title = NormalizeTitle(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ExtractFollowingTitle(lines, index);
            }

            chapters[number] = new SourceChapterReference(
                number,
                string.IsNullOrWhiteSpace(title) ? $"Chương {number}" : title);
        }

        var ordered = chapters.Values
            .OrderBy(chapter => chapter.Number)
            .ToList();

        if (ordered.Count < MinimumReliableChapterCount || ordered[0].Number != 1)
        {
            return Array.Empty<SourceChapterReference>();
        }

        var expectedRange = ordered[^1].Number;
        var missingCount = expectedRange - ordered.Count;
        if (missingCount > Math.Max(1, expectedRange / 10))
        {
            return Array.Empty<SourceChapterReference>();
        }

        return ordered;
    }

    private static string ExtractFollowingTitle(IReadOnlyList<string> lines, int headingIndex)
    {
        var titleLines = new List<string>();

        for (var offset = 1; offset <= 3 && headingIndex + offset < lines.Count; offset++)
        {
            var candidate = NormalizeTitle(lines[headingIndex + offset]);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.StartsWith("==", StringComparison.Ordinal) ||
                candidate.StartsWith("[Đoạn", StringComparison.OrdinalIgnoreCase) ||
                ChapterHeadingRegex().IsMatch(candidate))
            {
                break;
            }

            if (IsPageFurniture(candidate))
            {
                continue;
            }

            if (titleLines.Count > 0 && !LooksLikeUppercaseTitleLine(candidate))
            {
                break;
            }

            titleLines.Add(candidate);
            if (!LooksLikeUppercaseTitleLine(candidate))
            {
                break;
            }
        }

        return string.Join(" ", titleLines).Trim();
    }

    private static bool IsPageFurniture(string value)
    {
        return value.Contains(" - ", StringComparison.Ordinal) &&
            value.Any(char.IsDigit) &&
            value.Length < 90;
    }

    private static bool LooksLikeUppercaseTitleLine(string value)
    {
        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length == 0 || value.Length > 120 || value.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var uppercaseCount = letters.Count(char.IsUpper);
        return uppercaseCount >= Math.Ceiling(letters.Length * 0.7);
    }

    private static string NormalizeLine(string value)
    {
        return WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();
    }

    private static string NormalizeTitle(string value)
    {
        return NormalizeLine(value)
            .Trim(' ', '\t', '-', '–', '—', ':', '.', '•');
    }

    [GeneratedRegex(
        @"^(?:chương|chuong|chapter)\s+(?<number>\d{1,3})(?:\s*[:.\-–—]\s*(?<title>.+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterHeadingRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
