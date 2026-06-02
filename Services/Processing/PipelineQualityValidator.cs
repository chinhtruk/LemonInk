using System.Net;
using System.Text.RegularExpressions;
using ZenRead.Services.Summarization;

namespace ZenRead.Services.Processing;

public static partial class PipelineQualityValidator
{
    public static void EnsureExtractedTextIsUsable(string text)
    {
        var normalized = CollapseWhitespace(text);
        var wordCount = WordCount(normalized);
        var meaningfulCharacters = normalized.Count(char.IsLetterOrDigit);

        if (normalized.Length < 40 ||
            wordCount < 6 ||
            meaningfulCharacters < normalized.Length / 4)
        {
            throw new InvalidOperationException(
                "Nội dung trích xuất quá ngắn hoặc không đủ rõ để LemonInk tạo bản tóm tắt. Hãy kiểm tra chất lượng PDF/OCR rồi thử lại.");
        }
    }

    public static void EnsureSummaryIsComplete(BookSummarizationResult summary, int sourceCharacterCount = 0)
    {
        if (WordCount(summary.ShortSummary) < 6)
        {
            throw new InvalidOperationException(
                "Bản giới thiệu nhanh do AI tạo ra chưa đủ nội dung. Hãy thử tạo tóm tắt lại.");
        }

        if (WordCount(summary.LongSummary) < 20)
        {
            throw new InvalidOperationException(
                "Phần tổng quan do AI tạo ra quá ngắn để tạo audio đầy đủ. Hãy thử tạo tóm tắt lại.");
        }

        var usableChapterCount = summary.Chapters.Count(chapter =>
            !string.IsNullOrWhiteSpace(chapter.Title) &&
            WordCount(chapter.ContentHtml) >= 15);

        if (usableChapterCount == 0)
        {
            throw new InvalidOperationException(
                "AI chưa tạo đủ nội dung chương để đọc và tạo audio. Hãy thử tạo tóm tắt lại.");
        }

        if (!summary.KeyTakeaways.Any(takeaway => WordCount(takeaway) >= 3))
        {
            throw new InvalidOperationException(
                "AI chưa tạo đủ ý chính cho cuốn sách. Hãy thử tạo tóm tắt lại.");
        }

        if (summary.ExpectedSourceChapters.Count >= 3)
        {
            var usableChapterNumbers = summary.Chapters
                .Where(chapter =>
                    !string.IsNullOrWhiteSpace(chapter.Title) &&
                    WordCount(chapter.ContentHtml) >= 15)
                .Select(chapter => chapter.Number)
                .ToHashSet();
            var missingChapters = summary.ExpectedSourceChapters
                .Where(chapter => !usableChapterNumbers.Contains(chapter.Number))
                .ToList();

            if (missingChapters.Count > 0 || usableChapterCount < summary.ExpectedSourceChapters.Count)
            {
                var missingPreview = string.Join(
                    ", ",
                    missingChapters
                        .Take(5)
                        .Select(chapter => $"Chương {chapter.Number}"));
                var missingMessage = string.IsNullOrWhiteSpace(missingPreview)
                    ? string.Empty
                    : $" Còn thiếu {missingPreview}.";

                throw new InvalidOperationException(
                    $"Bản tóm tắt chưa bao phủ đủ mục lục gốc ({usableChapterCount}/{summary.ExpectedSourceChapters.Count} chương hợp lệ).{missingMessage} Hãy tạo tóm tắt lại.");
            }
        }

        var totalChapterCharacters = summary.Chapters.Sum(chapter =>
            CollapseWhitespace(ToPlainText(chapter.ContentHtml)).Length);

        if (sourceCharacterCount >= 300_000 &&
            (summary.Chapters.Count < 8 || totalChapterCharacters < 22_000))
        {
            throw new InvalidOperationException(
                "Bản tóm tắt của tài liệu dài còn quá cô đọng để phản ánh đủ nội dung chương. Hãy thử tạo tóm tắt lại.");
        }

        if (sourceCharacterCount >= 120_000 && totalChapterCharacters < 10_000)
        {
            throw new InvalidOperationException(
                "Bản tóm tắt của tài liệu dài chưa đủ chi tiết cho các chương. Hãy thử tạo tóm tắt lại.");
        }
    }

    private static int WordCount(string value)
    {
        return WordRegex().Matches(CollapseWhitespace(ToPlainText(value))).Count;
    }

    private static string ToPlainText(string value)
    {
        return WebUtility.HtmlDecode(HtmlTagRegex().Replace(value ?? string.Empty, " "));
    }

    private static string CollapseWhitespace(string value)
    {
        return WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
