using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZenRead.Entities;

namespace ZenRead.Services.Summarization;

public class OpenAiBookSummarizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly AiSummarizationOptions _options;

    public OpenAiBookSummarizationService(HttpClient httpClient, IOptions<AiSummarizationOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.OpenAI.ApiKey);

    public async Task<BookSummarizationResult> SummarizeAsync(
        Book book,
        IReadOnlyList<BookContentChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("OpenAI API key chưa được cấu hình.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.OpenAI.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.OpenAI.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = _options.OpenAI.Model,
            instructions = BuildInstructions(),
            input = BuildInput(book, chunks),
            max_output_tokens = _options.OpenAI.MaxOutputTokens,
            store = false
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI summarization failed: {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");
        }

        var outputText = ExtractOutputText(responseBody);
        var payload = ExtractJsonObject(outputText);
        var aiResult = JsonSerializer.Deserialize<AiSummaryPayload>(payload, JsonOptions)
            ?? throw new InvalidOperationException("Không đọc được JSON tóm tắt từ AI.");

        return MapResult(aiResult, book);
    }

    private static string BuildInstructions()
    {
        return """
        Bạn là LemonAI, trợ lý tóm tắt sách tiếng Việt cho LemonInk.
        Nhiệm vụ: đọc nội dung sách/tài liệu do người dùng upload và tạo bản tóm tắt rõ ràng, tự nhiên, có ích cho người đọc bận rộn.
        Chỉ trả về một JSON object hợp lệ, không markdown, không giải thích ngoài JSON.
        JSON phải có dạng:
        {
          "shortSummary": "tóm tắt giới thiệu nhanh hấp dẫn dài khoảng 4-6 câu để làm mô tả dẫn đề dưới tiêu đề",
          "longSummaryParagraphs": ["đoạn tổng quan 1", "đoạn tổng quan 2"],
          "keyTakeaways": ["ý chính 1", "ý chính 2", "ý chính 3", "ý chính 4", "ý chính 5"],
          "chapters": [
            {
              "number": 1,
              "title": "Tên phần/chương",
              "paragraphs": ["đoạn tóm tắt chương"],
              "readingTimeMinutes": 3
            }
          ]
        }
        Yêu cầu:
        - Viết hoàn toàn bằng tiếng Việt.
        - Không bịa dữ kiện ngoài nội dung được cung cấp.
        - shortSummary nên dài khoảng 4-6 câu (khoảng 80-120 từ), khái quát một cách cuốn hút và chi tiết hơn về chủ đề chính, bối cảnh, và giá trị/thông điệp cốt lõi của tác phẩm làm mô tả dẫn đề dưới tiêu đề.
        - Nếu nội dung không chia chương rõ, hãy chia thành 3-5 phần theo mạch ý.
        - Long summary chỉ nên có 2-3 đoạn ngắn, mỗi đoạn khoảng 45-75 từ. Phần này là tổng quan đầu trang, không thay thế nội dung chương.
        - Đưa phân tích chi tiết vào chapters thay vì làm tổng quan quá dài.
        - Key takeaways phải cụ thể, tránh câu chung chung.
        - Mỗi chapter nên có 1-3 đoạn ngắn.
        """;
    }

    private string BuildInput(Book book, IReadOnlyList<BookContentChunk> chunks)
    {
        var source = string.Join(
            "\n\n",
            chunks
                .OrderBy(chunk => chunk.ChunkIndex)
                .Select(chunk => $"[Đoạn {chunk.ChunkIndex + 1}]\n{chunk.Content}"));

        if (source.Length > _options.MaxInputCharacters)
        {
            source = source[.._options.MaxInputCharacters];
        }

        return $"""
        Tên sách/tài liệu: {book.Title}
        Tác giả: {book.AuthorName}
        Thể loại: {book.Category}
        Ngôn ngữ mong muốn: tiếng Việt

        Nội dung trích xuất:
        {source}
        """;
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputTextElement) &&
            outputTextElement.ValueKind == JsonValueKind.String)
        {
            return outputTextElement.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var outputElement) ||
            outputElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OpenAI response không có output text.");
        }

        var builder = new StringBuilder();
        foreach (var outputItem in outputElement.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentElement.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(textElement.GetString());
                }
            }
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("OpenAI response không trả về nội dung tóm tắt.");
        }

        return text;
    }

    private static string ExtractJsonObject(string value)
    {
        var text = value.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("AI không trả về JSON object hợp lệ.");
        }

        return text[start..(end + 1)];
    }

    private static BookSummarizationResult MapResult(AiSummaryPayload payload, Book book)
    {
        var longSummary = payload.LongSummaryParagraphs.Count == 0
            ? WebUtility.HtmlEncode(payload.ShortSummary)
            : string.Join("\n", payload.LongSummaryParagraphs.Select(paragraph => $"<p>{WebUtility.HtmlEncode(paragraph)}</p>"));

        var chapters = payload.Chapters.Count == 0
            ? new List<BookChapterDraft>
            {
                new()
                {
                    Number = 1,
                    Title = $"Tổng quan về {book.Title}",
                    ContentHtml = longSummary,
                    ReadingTimeMinutes = 3
                }
            }
            : payload.Chapters
                .OrderBy(chapter => chapter.Number)
                .Select((chapter, index) => new BookChapterDraft
                {
                    Number = chapter.Number > 0 ? chapter.Number : index + 1,
                    Title = string.IsNullOrWhiteSpace(chapter.Title) ? $"Phần {index + 1}" : chapter.Title.Trim(),
                    ContentHtml = chapter.Paragraphs.Count == 0
                        ? $"<p>{WebUtility.HtmlEncode(payload.ShortSummary)}</p>"
                        : string.Join("\n", chapter.Paragraphs.Select(paragraph => $"<p>{WebUtility.HtmlEncode(paragraph)}</p>")),
                    ReadingTimeMinutes = Math.Clamp(chapter.ReadingTimeMinutes, 1, 20)
                })
                .ToList();

        return new BookSummarizationResult
        {
            ShortSummary = string.IsNullOrWhiteSpace(payload.ShortSummary)
                ? $"{book.Title} đã được LemonAI tóm tắt từ nội dung upload."
                : payload.ShortSummary.Trim(),
            LongSummary = longSummary,
            GeneratedBy = "OpenAI Responses API",
            KeyTakeaways = payload.KeyTakeaways
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Take(7)
                .ToList(),
            Chapters = chapters
        };
    }

    private sealed class AiSummaryPayload
    {
        public string ShortSummary { get; set; } = string.Empty;

        public List<string> LongSummaryParagraphs { get; set; } = new();

        public List<string> KeyTakeaways { get; set; } = new();

        public List<AiChapterPayload> Chapters { get; set; } = new();
    }

    private sealed class AiChapterPayload
    {
        public int Number { get; set; }

        public string Title { get; set; } = string.Empty;

        public List<string> Paragraphs { get; set; } = new();

        public int ReadingTimeMinutes { get; set; } = 3;
    }
}
