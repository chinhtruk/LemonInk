using System.Net;
using System.Text.RegularExpressions;
using ZenRead.Entities;
using ZenRead.ViewModels;

namespace ZenRead.Services.Books;

public static partial class ReadingTimeCalculator
{
    public static int FromAudioDurationSeconds(int durationSeconds)
    {
        return Math.Max(1, (int)Math.Ceiling(durationSeconds / 60d));
    }

    public static int GetDisplayMinutes(int storedReadingMinutes, int? audioDurationSeconds)
    {
        return audioDurationSeconds is > 0
            ? FromAudioDurationSeconds(audioDurationSeconds.Value)
            : Math.Max(1, storedReadingMinutes);
    }

    public static void SyncWithAudio(Book book, IReadOnlyList<BookSummarySection> sections, int durationSeconds)
    {
        var totalMinutes = FromAudioDurationSeconds(durationSeconds);
        book.ReadingTimeMinutes = totalMinutes;

        var chapters = sections
            .Where(section => section.SectionType == SummarySectionType.Chapter)
            .OrderBy(section => section.SortOrder)
            .ToList();

        if (chapters.Count == 0)
        {
            return;
        }

        var weights = chapters
            .Select(chapter => Math.Max(1, ToPlainText(chapter.ContentHtml).Length))
            .ToList();

        var allocatedMinutes = DistributeMinutes(totalMinutes, weights);
        for (var index = 0; index < chapters.Count; index++)
        {
            chapters[index].ReadingTimeMinutes = allocatedMinutes[index];
            chapters[index].UpdatedAt = DateTime.UtcNow;
        }
    }

    public static List<int> EstimateChapterMinutesFromAudio(
        IReadOnlyList<Chapter> chapters,
        int? audioDurationSeconds)
    {
        if (chapters.Count == 0 || audioDurationSeconds is not > 0)
        {
            return chapters.Select(chapter => Math.Max(1, chapter.ReadingTimeMinutes)).ToList();
        }

        var weights = chapters
            .Select(chapter => Math.Max(1, ToPlainText(chapter.Content).Length))
            .ToList();

        return DistributeMinutes(FromAudioDurationSeconds(audioDurationSeconds.Value), weights);
    }

    private static List<int> DistributeMinutes(int totalMinutes, IReadOnlyList<int> weights)
    {
        if (weights.Count == 0)
        {
            return new List<int>();
        }

        if (totalMinutes <= weights.Count)
        {
            return Enumerable.Repeat(1, weights.Count).ToList();
        }

        var result = Enumerable.Repeat(1, weights.Count).ToList();
        var remainingMinutes = totalMinutes - weights.Count;
        var totalWeight = weights.Sum();
        var remainders = new List<(int Index, double Value)>();

        for (var index = 0; index < weights.Count; index++)
        {
            var exact = remainingMinutes * (weights[index] / (double)totalWeight);
            var whole = (int)Math.Floor(exact);
            result[index] += whole;
            remainders.Add((index, exact - whole));
        }

        var allocated = result.Sum();
        foreach (var item in remainders.OrderByDescending(item => item.Value))
        {
            if (allocated >= totalMinutes)
            {
                break;
            }

            result[item.Index] += 1;
            allocated += 1;
        }

        return result;
    }

    private static string ToPlainText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
