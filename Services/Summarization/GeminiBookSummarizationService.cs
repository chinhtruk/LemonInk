using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Ai;

namespace ZenRead.Services.Summarization;

public class GeminiBookSummarizationService
{
    private const int MaxProviderRetries = 3;
    private const int LongDocumentCharacterThreshold = 120_000;
    private const int VeryLongDocumentCharacterThreshold = 300_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IAiModelRouter _modelRouter;
    private readonly AiSummarizationOptions _options;
    private readonly ApplicationDbContext _dbContext;

    public GeminiBookSummarizationService(
        HttpClient httpClient,
        IAiModelRouter modelRouter,
        IOptions<AiSummarizationOptions> options,
        ApplicationDbContext dbContext)
    {
        _httpClient = httpClient;
        _modelRouter = modelRouter;
        _options = options.Value;
        _dbContext = dbContext;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    public async Task<BookSummarizationResult> SummarizeAsync(
        Book book,
        IReadOnlyList<BookContentChunk> chunks,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("LemonAI API key chưa được cấu hình.");
        }

        var source = BuildSource(chunks);
        EnsureFullSourceIsSupported(source);
        var sourceChapters = SourceChapterOutlineExtractor.Extract(source);
        var maximumPassCharacters = Math.Clamp(_options.MaxCharactersPerSummaryPass, 40000, 200000);
        if (source.Length <= maximumPassCharacters)
        {
            var payload = await GeneratePayloadAsync(
                apiKey,
                BuildPrompt(book, source, isLongDocumentSynthesis: false, source.Length, sourceChapters),
                cancellationToken);
            return MapResult(payload, book, sourceChapters);
        }

        var sourcePasses = BuildSourcePasses(chunks, maximumPassCharacters);
        var partialSummaries = new List<AiSummaryPayload>(sourcePasses.Count);
        for (var index = 0; index < sourcePasses.Count; index++)
        {
            var passIndex = index + 1;
            var sourceHash = ComputeSourceHash(sourcePasses[index]);
            var partialSummary = await LoadCheckpointAsync(
                book.Id,
                passIndex,
                sourcePasses.Count,
                sourceHash,
                cancellationToken);

            if (partialSummary is null)
            {
                var prompt = BuildPartialPrompt(
                    book,
                    sourcePasses[index],
                    passIndex,
                    sourcePasses.Count,
                    source.Length,
                    sourceChapters);
                partialSummary = await GeneratePayloadAsync(apiKey, prompt, cancellationToken);
                await SaveCheckpointAsync(
                    book.Id,
                    passIndex,
                    sourcePasses.Count,
                    sourceHash,
                    partialSummary,
                    cancellationToken);

                if (passIndex < sourcePasses.Count)
                {
                    await Task.Delay(ResolveMultiPassDelay(), cancellationToken);
                }
            }

            partialSummaries.Add(partialSummary);
        }

        await Task.Delay(ResolveMultiPassDelay(), cancellationToken);
        var synthesisSource = BuildSynthesisSource(partialSummaries);
        var synthesisPayload = await GeneratePayloadAsync(
            apiKey,
            BuildPrompt(book, synthesisSource, isLongDocumentSynthesis: true, source.Length, sourceChapters),
            cancellationToken);

        return MapResult(synthesisPayload, book, sourceChapters);
    }

    private async Task<AiSummaryPayload?> LoadCheckpointAsync(
        int bookId,
        int passIndex,
        int passCount,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _dbContext.BookSummaryPassCheckpoints
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.BookId == bookId &&
                    item.PassIndex == passIndex &&
                    item.PassCount == passCount &&
                    item.SourceHash == sourceHash,
                cancellationToken);

        if (checkpoint is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiSummaryPayload>(checkpoint.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveCheckpointAsync(
        int bookId,
        int passIndex,
        int passCount,
        string sourceHash,
        AiSummaryPayload payload,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _dbContext.BookSummaryPassCheckpoints
            .SingleOrDefaultAsync(
                item => item.BookId == bookId && item.PassIndex == passIndex,
                cancellationToken);

        if (checkpoint is null)
        {
            checkpoint = new BookSummaryPassCheckpoint
            {
                BookId = bookId,
                PassIndex = passIndex,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.BookSummaryPassCheckpoints.Add(checkpoint);
        }

        checkpoint.PassCount = passCount;
        checkpoint.SourceHash = sourceHash;
        checkpoint.PayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        checkpoint.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<AiSummaryPayload> GeneratePayloadAsync(
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.25,
                maxOutputTokens = _options.Gemini.MaxOutputTokens,
                responseMimeType = "application/json",
                responseSchema = BuildResponseSchema()
            }
        };

        Exception? lastParseException = null;
        Exception? lastProviderException = null;
        foreach (var model in await _modelRouter.OrderModelsAsync(AiModelTask.Summarization, ResolveSummarizationModels(), cancellationToken))
        {
            var endpoint = BuildEndpoint(apiKey, model);
            for (var attempt = 1; attempt <= MaxProviderRetries; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.PostAsJsonAsync(endpoint, request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var providerException = new InvalidOperationException(
                        BuildProviderErrorMessage(response.StatusCode, response.ReasonPhrase, model, responseBody));

                    if (IsAuthenticationError(response.StatusCode))
                    {
                        await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, providerException, stopwatch.Elapsed, cancellationToken);
                        throw providerException;
                    }

                    lastProviderException = providerException;
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, providerException, stopwatch.Elapsed, cancellationToken);
                    // A provider/quota error is better served by the next configured model.
                    // Retrying the same model burns scarce free-tier RPM/RPD before fallback.
                    break;
                }

                if (string.IsNullOrWhiteSpace(responseBody))
                {
                    lastProviderException = new InvalidOperationException("LemonAI chưa trả về nội dung tóm tắt. Bạn thử retry sau ít phút.");
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, lastProviderException, stopwatch.Elapsed, cancellationToken);
                    break;
                }

                try
                {
                    var outputText = ExtractOutputText(responseBody);
                    var payload = ExtractJsonObject(outputText);
                    var aiResult = JsonSerializer.Deserialize<AiSummaryPayload>(payload, JsonOptions)
                        ?? throw new InvalidOperationException("Không đọc được JSON tóm tắt từ LemonAI.");

                    await _modelRouter.ReportSuccessAsync(AiModelTask.Summarization, model, stopwatch.Elapsed, cancellationToken);
                    return aiResult;
                }
                catch (JsonException exception) when (attempt < MaxProviderRetries)
                {
                    lastParseException = exception;
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, exception, stopwatch.Elapsed, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                }
                catch (JsonException exception)
                {
                    lastParseException = exception;
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, exception, stopwatch.Elapsed, cancellationToken);
                    break;
                }
                catch (InvalidOperationException exception) when (attempt < MaxProviderRetries && IsMalformedJsonError(exception))
                {
                    lastParseException = exception;
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, exception, stopwatch.Elapsed, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                }
                catch (InvalidOperationException exception) when (IsMalformedJsonError(exception))
                {
                    lastParseException = exception;
                    await _modelRouter.ReportFailureAsync(AiModelTask.Summarization, model, exception, stopwatch.Elapsed, cancellationToken);
                    break;
                }
            }
        }

        if (lastProviderException is not null)
        {
            throw lastProviderException;
        }

        throw new InvalidOperationException(
            "LemonAI trả về tóm tắt chưa đúng định dạng JSON sau nhiều lần thử. Bạn thử retry lại sau ít phút.",
            lastParseException);
    }

    private static object BuildResponseSchema()
    {
        return new
        {
            type = "OBJECT",
            required = new[] { "shortSummary", "longSummaryParagraphs", "keyTakeaways", "chapters" },
            properties = new
            {
                shortSummary = new { type = "STRING" },
                longSummaryParagraphs = new
                {
                    type = "ARRAY",
                    items = new { type = "STRING" }
                },
                keyTakeaways = new
                {
                    type = "ARRAY",
                    items = new { type = "STRING" }
                },
                chapters = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        required = new[] { "number", "title", "paragraphs", "readingTimeMinutes" },
                        properties = new
                        {
                            number = new { type = "INTEGER" },
                            title = new { type = "STRING" },
                            paragraphs = new
                            {
                                type = "ARRAY",
                                items = new { type = "STRING" }
                            },
                            readingTimeMinutes = new { type = "INTEGER" }
                        }
                    }
                }
            }
        };
    }

    private string? GetApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.Gemini.ApiKey))
        {
            return _options.Gemini.ApiKey;
        }

        return Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    }

    private string BuildEndpoint(string apiKey, string modelName)
    {
        var baseUrl = _options.Gemini.EndpointBaseUrl.TrimEnd('/');
        var model = Uri.EscapeDataString(modelName);
        var encodedKey = Uri.EscapeDataString(apiKey);

        return $"{baseUrl}/{model}:generateContent?key={encodedKey}";
    }

    private IReadOnlyList<string> ResolveSummarizationModels()
    {
        var models = new List<string>();
        models.AddRange(_options.Gemini.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim()));

        if (!string.IsNullOrWhiteSpace(_options.Gemini.Model))
        {
            models.Add(_options.Gemini.Model.Trim());
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildProviderErrorMessage(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string model,
        string responseBody)
    {
        var body = string.IsNullOrWhiteSpace(responseBody)
            ? string.Empty
            : $" {responseBody}";

        return $"LemonAI chưa tạo được tóm tắt bằng {model} ({(int)statusCode} {reasonPhrase}).{body}";
    }

    private static bool IsAuthenticationError(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private static string BuildSource(IReadOnlyList<BookContentChunk> chunks)
    {
        return string.Join(
            "\n\n",
            chunks
                .OrderBy(chunk => chunk.ChunkIndex)
                .Select(chunk => $"[Đoạn {chunk.ChunkIndex + 1}]\n{chunk.Content}"));
    }

    private void EnsureFullSourceIsSupported(string source)
    {
        if (source.Length > _options.MaxInputCharacters)
        {
            throw new InvalidOperationException(
                $"Nội dung sách có {source.Length:N0} ký tự, vượt giới hạn xử lý {_options.MaxInputCharacters:N0} ký tự. " +
                "LemonInk đã dừng để tránh sinh bản tóm tắt không đầy đủ. Hãy chia tài liệu thành các phần nhỏ hơn.");
        }
    }

    private static List<string> BuildSourcePasses(
        IReadOnlyList<BookContentChunk> chunks,
        int maximumPassCharacters)
    {
        var passes = new List<string>();
        var builder = new StringBuilder();
        foreach (var chunk in chunks.OrderBy(item => item.ChunkIndex))
        {
            var block = $"[Đoạn {chunk.ChunkIndex + 1}]\n{chunk.Content}";
            if (builder.Length > 0 && builder.Length + block.Length + 2 > maximumPassCharacters)
            {
                passes.Add(builder.ToString());
                builder.Clear();
            }

            if (block.Length <= maximumPassCharacters)
            {
                if (builder.Length > 0)
                {
                    builder.Append("\n\n");
                }

                builder.Append(block);
                continue;
            }

            for (var start = 0; start < block.Length; start += maximumPassCharacters)
            {
                if (builder.Length > 0)
                {
                    passes.Add(builder.ToString());
                    builder.Clear();
                }

                passes.Add(block.Substring(start, Math.Min(maximumPassCharacters, block.Length - start)));
            }
        }

        if (builder.Length > 0)
        {
            passes.Add(builder.ToString());
        }

        return passes;
    }

    private TimeSpan ResolveMultiPassDelay()
    {
        return TimeSpan.FromMilliseconds(Math.Clamp(_options.MultiPassDelayMilliseconds, 0, 30000));
    }

    private static string ComputeSourceHash(string source)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static string BuildSynthesisSource(IReadOnlyList<AiSummaryPayload> partialSummaries)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < partialSummaries.Count; index++)
        {
            var partial = partialSummaries[index];
            builder.AppendLine($"[Kết quả đọc phần {index + 1}]");
            builder.AppendLine(partial.ShortSummary);
            foreach (var paragraph in partial.LongSummaryParagraphs)
            {
                builder.AppendLine(paragraph);
            }

            foreach (var chapter in partial.Chapters.OrderBy(item => item.Number))
            {
                builder.AppendLine($"- {chapter.Title}");
                foreach (var paragraph in chapter.Paragraphs)
                {
                    builder.AppendLine(paragraph);
                }
            }

            foreach (var takeaway in partial.KeyTakeaways)
            {
                builder.AppendLine($"* {takeaway}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string BuildPartialPrompt(
        Book book,
        string source,
        int passNumber,
        int passCount,
        int totalSourceCharacters,
        IReadOnlyList<SourceChapterReference> sourceChapters)
    {
        var chapterRetentionGuidance = sourceChapters.Count > 0
            ? "- Khi phần nguồn này đi qua một hoặc nhiều chương trong mục lục gốc, giữ riêng từng chương thực sự xuất hiện và đúng số chương/tên chương; không gộp các chương khác nhau. Mỗi chương giữ 2-4 đoạn có luận điểm và ví dụ quan trọng để bước tổng hợp cuối không mất ý."
            : totalSourceCharacters >= VeryLongDocumentCharacterThreshold
                ? "- Toàn sách rất dài, vì vậy chapters của phần này phải có 4-7 mục, mỗi mục 3-5 đoạn chi tiết, mỗi đoạn 55-100 từ; giữ các ví dụ và luận cứ để lần tổng hợp cuối không làm mất ý."
                : "- chapters phản ánh các mạch/chương thực sự xuất hiện trong phần này; tạo 3-6 mục, mỗi mục 2-4 đoạn chi tiết.";
        var outlineGuidance = BuildOutlineGuidance(sourceChapters, requireFullCoverage: false);

        return $$"""
        Bạn là LemonAI đang đọc phần {{passNumber}}/{{passCount}} của một cuốn sách dài cho LemonInk.

        Hãy đọc kỹ phần nội dung dưới đây và trả về đúng một JSON object hợp lệ theo schema được yêu cầu.
        Đây là bản ghi trung gian để tổng hợp toàn cuốn sách, vì vậy phải giữ lại nhân vật, khái niệm, lập luận,
        diễn biến, ví dụ, tên chương hoặc chi tiết quan trọng xuất hiện trong chính phần này. Không bịa thông tin
        từ các phần chưa được cung cấp và không viết markdown.

        Yêu cầu cho dữ liệu trung gian:
        - Viết hoàn toàn bằng tiếng Việt.
        - shortSummary tóm tắt phạm vi phần này trong 2-3 câu.
        - longSummaryParagraphs có 3-5 đoạn, mỗi đoạn 50-100 từ.
        - keyTakeaways có 6-10 ý cụ thể.
        {{chapterRetentionGuidance}}
        {{outlineGuidance}}

        Tên sách/tài liệu: {{book.Title}}
        Tác giả: {{book.AuthorName}}
        Thể loại: {{book.Category}}
        Phạm vi đang đọc: phần {{passNumber}} trên tổng số {{passCount}} phần.

        Nội dung của phần này:
        {{source}}
        """;
    }

    private string BuildPrompt(
        Book book,
        string source,
        bool isLongDocumentSynthesis,
        int totalSourceCharacters,
        IReadOnlyList<SourceChapterReference> sourceChapters)
    {
        var chapterGuidance = ResolveChapterGuidance(isLongDocumentSynthesis, totalSourceCharacters, sourceChapters);
        var outlineGuidance = BuildOutlineGuidance(sourceChapters, requireFullCoverage: true);
        var sourceScaleGuidance = totalSourceCharacters >= VeryLongDocumentCharacterThreshold
            ? $"Nguồn gốc có khoảng {totalSourceCharacters:N0} ký tự, thuộc loại sách rất dài. Kết quả phải là bản tóm tắt đọc hiểu có chiều sâu, không phải bản lược ý vài trang."
            : totalSourceCharacters >= LongDocumentCharacterThreshold
                ? $"Nguồn gốc có khoảng {totalSourceCharacters:N0} ký tự. Cần giữ đủ các mạch chính và ví dụ quan trọng thay vì nén quá mạnh."
                : string.Empty;

        return $$"""
        Bạn là LemonAI, trợ lý tóm tắt sách tiếng Việt cho LemonInk.

        Hãy đọc kỹ {{(isLongDocumentSynthesis ? "bản ghi đầy đủ được tổng hợp tuần tự từ toàn bộ các phần của sách dài" : "toàn bộ nội dung sách/tài liệu bên dưới")}} và chỉ trả về một JSON object hợp lệ, không markdown, không giải thích ngoài JSON.
        Mục tiêu là tạo một bản tóm tắt đủ sâu để người đọc nắm được phần lớn nội dung quan trọng, không phải bản giới thiệu ngắn.
        {{sourceScaleGuidance}}

        JSON bắt buộc có dạng:
        {
          "shortSummary": "giới thiệu nhanh hấp dẫn, cô đọng trong 2-3 câu và không quá khoảng 55 từ để hiển thị dưới tiêu đề sách",
          "longSummaryParagraphs": ["đoạn tổng hợp chi tiết hỗ trợ audio/ngữ cảnh 1", "đoạn tổng hợp chi tiết hỗ trợ audio/ngữ cảnh 2"],
          "keyTakeaways": ["ý chính cụ thể 1", "ý chính cụ thể 2", "ý chính cụ thể 3"],
          "chapters": [
            {
              "number": 1,
              "title": "Tên phần/chương",
              "paragraphs": ["đoạn tóm tắt chi tiết của chương"],
              "readingTimeMinutes": 3
            }
          ]
        }

        Yêu cầu:
        - Viết hoàn toàn bằng tiếng Việt.
        - Không bịa dữ kiện ngoài nội dung được cung cấp.
        - Không tóm tắt vắn tắt quá mức. Giữ lại tên nhân vật/tác giả/khái niệm/sự kiện/luận điểm/các ví dụ quan trọng nếu có trong nội dung.
        - shortSummary chỉ gồm 2-3 câu ngắn gọn (tối đa khoảng 55 từ), nêu chủ đề trọng tâm và giá trị cốt lõi của tác phẩm để hiển thị ngay dưới tiêu đề. Không kể lại diễn biến, không liệt kê nhiều ý, không đưa phân tích chi tiết vào đây.
        - longSummaryParagraphs chỉ có 2-3 đoạn, mỗi đoạn 45-75 từ. Phần này dùng làm ngữ cảnh tổng hợp và kịch bản audio, không phải phần giới thiệu hiển thị ngay dưới tiêu đề.
        - Đưa phần phân tích chi tiết, ví dụ, diễn biến và luận điểm phụ vào chapters, không nhồi hết vào longSummaryParagraphs.
        - {{chapterGuidance}}
        {{outlineGuidance}}
        - Nếu nội dung đã có chương/phần rõ ràng, giữ đúng tên chương/phần gần nhất.
        - Mỗi chapter phải nêu ý chính, giải thích chi tiết, dẫn ví dụ hoặc chi tiết then chốt và nêu ý nghĩa của chương.
        - keyTakeaways phải có 7-10 ý, mỗi ý là một câu cụ thể, tránh câu chung chung như "cuốn sách rất hữu ích".
        - Ưu tiên chính xác và đầy đủ thông tin hơn là ngắn gọn. Chỉ rút gọn những phần lặp lại hoặc ít giá trị.
        - Nếu nội dung là tác phẩm văn học, phải giữ lại hình tượng, cảm xúc, chi tiết nghệ thuật, chủ đề và nhận định chính.
        - Nếu nội dung là sách kỹ năng/kinh doanh/khoa học, phải giữ lại khái niệm, nguyên nhân, hệ quả, ví dụ và cách áp dụng.

        Tên sách/tài liệu: {{book.Title}}
        Tác giả: {{book.AuthorName}}
        Thể loại: {{book.Category}}

        {{(isLongDocumentSynthesis ? "Ghi chép trung gian đã bao phủ toàn bộ sách:" : "Nội dung trích xuất:")}}
        {{source}}
        """;
    }

    private static string ResolveChapterGuidance(
        bool isLongDocumentSynthesis,
        int totalSourceCharacters,
        IReadOnlyList<SourceChapterReference> sourceChapters)
    {
        if (sourceChapters.Count > 0)
        {
            return $"Nguồn có mục lục đáng tin cậy gồm {sourceChapters.Count} chương. Trả về đúng {sourceChapters.Count} chapter theo đúng số chương và tên chương gốc; không gộp chương, không bỏ chương, không thay bằng chủ đề tổng hợp. Mỗi chapter cần 2-3 đoạn, mỗi đoạn 55-85 từ, giữ luận điểm và ví dụ quan trọng của riêng chương đó.";
        }

        if (!isLongDocumentSynthesis)
        {
            return "Nếu nội dung không chia chương rõ, hãy chia thành 4-7 phần theo mạch ý thật sự, đặt title thân thiện như tên chương/phần, không dùng \"Đoạn 1\".";
        }

        if (totalSourceCharacters >= VeryLongDocumentCharacterThreshold)
        {
            return "Vì đây là sách rất dài đã được đọc qua nhiều phần, hãy giữ tên chương thật nếu nhận diện được hoặc tạo 12-18 chương/phần tổng hợp theo đúng mạch nội dung. Mỗi chapter phải có 4-5 đoạn, mỗi đoạn 60-100 từ; bao gồm luận điểm, diễn giải, ví dụ/chi tiết then chốt và ý nghĩa ứng dụng. Không nén một chapter xuống chỉ còn 1-2 đoạn.";
        }

        if (totalSourceCharacters >= LongDocumentCharacterThreshold)
        {
            return "Vì đây là sách dài đã được đọc qua nhiều phần, hãy tạo 8-14 chương/phần tổng hợp theo mạch nội dung, mỗi chapter có 3-5 đoạn, mỗi đoạn 60-105 từ.";
        }

        return "Hãy tạo các chương/phần theo mạch nội dung, mỗi chapter có 2-4 đoạn, mỗi đoạn 60-105 từ.";
    }

    private static string BuildOutlineGuidance(
        IReadOnlyList<SourceChapterReference> sourceChapters,
        bool requireFullCoverage)
    {
        if (sourceChapters.Count == 0)
        {
            return string.Empty;
        }

        var directive = requireFullCoverage
            ? "Mục lục nhận diện từ tài liệu gốc dưới đây là ràng buộc bắt buộc cho mảng chapters cuối cùng. Mỗi dòng phải tương ứng một chapter JSON, giữ nguyên number và title:"
            : "Tài liệu gốc có mục lục dưới đây. Khi phần đang đọc thuộc một chương trong danh sách, hãy giữ đúng number và title để bước tổng hợp cuối không mất chương:";
        var outline = string.Join(
            "\n",
            sourceChapters.Select(chapter => $"- Chương {chapter.Number}: {chapter.Title}"));

        return $"{directive}\n{outline}";
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("candidates", out var candidatesElement) ||
            candidatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("LemonAI response không có candidates.");
        }

        var builder = new StringBuilder();
        foreach (var candidate in candidatesElement.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var contentElement) ||
                !contentElement.TryGetProperty("parts", out var partsElement) ||
                partsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(textElement.GetString());
                }
            }
        }

        var text = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("LemonAI response không trả về nội dung tóm tắt.");
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
            throw new InvalidOperationException("LemonAI không trả về JSON object hợp lệ.");
        }

        return text[start..(end + 1)];
    }

    private static bool IsMalformedJsonError(Exception exception)
    {
        return exception.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase);
    }

    private static BookSummarizationResult MapResult(
        AiSummaryPayload payload,
        Book book,
        IReadOnlyList<SourceChapterReference> expectedSourceChapters)
    {
        var longSummary = payload.LongSummaryParagraphs.Count == 0
            ? $"<p>{WebUtility.HtmlEncode(payload.ShortSummary)}</p>"
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
            GeneratedBy = "LemonAI",
            KeyTakeaways = payload.KeyTakeaways
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Take(10)
                .ToList(),
            Chapters = chapters,
            ExpectedSourceChapters = expectedSourceChapters
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
