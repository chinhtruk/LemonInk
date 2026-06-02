using System.Net;
using System.Text.RegularExpressions;
using ZenRead.Entities;

namespace ZenRead.Services.Summarization;

public partial class DraftBookSummarizationService : IBookSummarizationService
{
    public Task<BookSummarizationResult> SummarizeAsync(
        Book book,
        IReadOnlyList<BookContentChunk> chunks,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Summarize(book, chunks));
    }

    public BookSummarizationResult Summarize(Book book, IReadOnlyList<BookContentChunk> chunks)
    {
        var sourceText = string.Join("\n\n", chunks.OrderBy(chunk => chunk.ChunkIndex).Select(chunk => chunk.Content));
        var sentences = SplitSentences(sourceText);
        var topSentences = PickRepresentativeSentences(sentences, 8);

        var shortSummary = BuildShortSummary(book, topSentences);
        var longSummary = BuildLongSummary(book, topSentences);
        var takeaways = BuildTakeaways(topSentences);
        var chapters = BuildChapters(book, chunks);

        return new BookSummarizationResult
        {
            ShortSummary = shortSummary,
            LongSummary = longSummary,
            GeneratedBy = "LemonInk draft summarizer",
            KeyTakeaways = takeaways,
            Chapters = chapters
        };
    }

    private static string BuildShortSummary(Book book, IReadOnlyList<string> sentences)
    {
        var first = sentences.FirstOrDefault() ?? "Tài liệu này đã được LemonInk trích xuất và chuẩn bị để tóm tắt.";
        return $"{book.Title} tập trung vào những ý chính nổi bật trong tài liệu: {first}";
    }

    private static string BuildLongSummary(Book book, IReadOnlyList<string> sentences)
    {
        var selected = sentences.Take(5).ToList();
        if (selected.Count == 0)
        {
            return $"LemonInk đã nhận nội dung của {book.Title}. Bản tóm tắt nháp sẽ được thay bằng bản AI đầy đủ khi provider AI được cấu hình.";
        }

        var paragraphs = selected
            .Select(sentence => $"<p>{WebUtility.HtmlEncode(sentence)}</p>");

        return string.Join("\n", paragraphs);
    }

    private static List<string> BuildTakeaways(IReadOnlyList<string> sentences)
    {
        var takeaways = sentences
            .Take(5)
            .Select(sentence => TrimSentence(sentence, 220))
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        while (takeaways.Count < 5)
        {
            takeaways.Add("Nội dung đã được trích xuất và sẵn sàng cho bước tóm tắt AI chi tiết hơn.");
        }

        return takeaways;
    }

    private static List<BookChapterDraft> BuildChapters(Book book, IReadOnlyList<BookContentChunk> chunks)
    {
        var orderedChunks = chunks.OrderBy(chunk => chunk.ChunkIndex).ToList();
        if (orderedChunks.Count == 0)
        {
            return new List<BookChapterDraft>();
        }

        var chapterCount = Math.Clamp(orderedChunks.Count, 1, 5);
        var chapters = new List<BookChapterDraft>();
        var chunkGroups = orderedChunks
            .Select((chunk, index) => new { chunk, group = index * chapterCount / orderedChunks.Count })
            .GroupBy(item => item.group)
            .OrderBy(group => group.Key);

        foreach (var group in chunkGroups)
        {
            var number = group.Key + 1;
            var text = string.Join(" ", group.Select(item => item.chunk.Content));
            var sentences = SplitSentences(text);
            var representative = PickRepresentativeSentences(sentences, 4);
            var content = representative.Count > 0
                ? string.Join("\n", representative.Select(sentence => $"<p>{WebUtility.HtmlEncode(sentence)}</p>"))
                : $"<p>{WebUtility.HtmlEncode(TrimSentence(text, 520))}</p>";

            chapters.Add(new BookChapterDraft
            {
                Number = number,
                Title = number == 1 ? $"Tổng quan về {book.Title}" : $"Phần {number}: Ý chính nổi bật",
                ContentHtml = content,
                ReadingTimeMinutes = EstimateReadingMinutes(text)
            });
        }

        return chapters;
    }

    private static List<string> SplitSentences(string text)
    {
        return SentenceSplitRegex()
            .Split(WhitespaceRegex().Replace(text, " ").Trim())
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length >= 35)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
    }

    private static List<string> PickRepresentativeSentences(IReadOnlyList<string> sentences, int count)
    {
        return sentences
            .Select((sentence, index) => new
            {
                sentence,
                index,
                score = ScoreSentence(sentence, index)
            })
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.index)
            .Take(count)
            .OrderBy(item => item.index)
            .Select(item => item.sentence)
            .ToList();
    }

    private static int ScoreSentence(string sentence, int index)
    {
        var lower = sentence.ToLowerInvariant();
        var score = Math.Max(0, 120 - index);
        var keywords = new[]
        {
            "quan trọng", "cốt lõi", "ý chính", "kết luận", "nguyên tắc", "phương pháp",
            "bài học", "hệ thống", "quyết định", "thói quen", "tư duy", "chiến lược"
        };

        foreach (var keyword in keywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                score += 40;
            }
        }

        if (sentence.Length is > 80 and < 260)
        {
            score += 20;
        }

        return score;
    }

    private static string TrimSentence(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].TrimEnd() + "...";
    }

    private static int EstimateReadingMinutes(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(words / 220.0));
    }

    [GeneratedRegex("(?<=[.!?。！？])\\s+")]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
