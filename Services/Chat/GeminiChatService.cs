using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Ai;
using ZenRead.Services.Summarization;

namespace ZenRead.Services.Chat;

public class GeminiChatService : IChatService
{
    private const int MaxQuestionLength = 1200;
    private const int MaxContextCharacters = 9000;
    private const int MaxContextItems = 6;
    private const int MaxHistoryMessages = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "anh", "ban", "cua", "cho", "cac", "con", "cung", "day", "duoc", "hay", "hoi", "khi",
        "la", "lai", "mot", "nay", "nhu", "nhung", "noi", "sach", "thi", "the", "trong", "toi",
        "ve", "voi", "what", "when", "where", "which", "this", "that", "book"
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IAiModelRouter _modelRouter;
    private readonly AiSummarizationOptions _options;

    public GeminiChatService(
        ApplicationDbContext dbContext,
        HttpClient httpClient,
        IAiModelRouter modelRouter,
        IOptions<AiSummarizationOptions> options)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
        _modelRouter = modelRouter;
        _options = options.Value;
    }

    public async Task<ChatServiceResult> AskAsync(
        string userId,
        int bookId,
        string message,
        CancellationToken cancellationToken)
    {
        var question = NormalizeQuestion(message);
        if (string.IsNullOrWhiteSpace(question))
        {
            return ChatServiceResult.Fail("Bạn nhập câu hỏi trước nhé.");
        }

        var book = await LoadBookAsync(bookId, cancellationToken);
        if (book is null)
        {
            return new ChatServiceResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new ChatServiceResult { Forbidden = true };
        }

        if (!book.IsSummaryReady)
        {
            return ChatServiceResult.Fail("Sách này chưa có bản tóm tắt để LemonAI đọc cùng bạn.");
        }

        var session = await GetOrCreateSessionAsync(userId, book, cancellationToken);
        var history = await GetRecentMessagesAsync(session.Id, cancellationToken);
        var contextItems = SelectRelevantContext(book, question);
        var prompt = BuildPrompt(book, question, contextItems, history);
        var answer = await BuildAnswerAsync(book, question, contextItems, prompt, cancellationToken);

        var userMessage = new ChatMessage
        {
            ChatSession = session,
            Role = ChatMessageRole.User,
            Content = question
        };

        var assistantMessage = new ChatMessage
        {
            ChatSession = session,
            Role = ChatMessageRole.Assistant,
            Content = answer,
            Citations = new List<ChatCitation>()
        };

        session.UpdatedAt = DateTime.UtcNow;
        _dbContext.ChatMessages.AddRange(userMessage, assistantMessage);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ChatServiceResult.Success(ToDto(assistantMessage, contextItems));
    }

    private async Task<string> BuildAnswerAsync(
        Book book,
        string question,
        IReadOnlyList<ChatContextItem> contextItems,
        string prompt,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "LemonAI chưa được cấu hình API key nên chưa thể trả lời bằng mô hình AI. Bạn kiểm tra lại GEMINI_API_KEY hoặc AI__Gemini__ApiKey nhé.";
        }

        try
        {
            var answer = await AskGeminiAsync(apiKey, prompt, cancellationToken);
            return SanitizeAssistantAnswer(answer);
        }
        catch (Exception exception) when (ShouldUseLocalFallback(exception))
        {
            return exception.Message;
        }
    }

    public async Task<ChatServiceResult> GetHistoryAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new ChatServiceResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new ChatServiceResult { Forbidden = true };
        }

        var session = await _dbContext.ChatSessions
            .AsNoTracking()
            .Where(item => item.UserId == userId && item.BookId == bookId)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session is null)
        {
            return ChatServiceResult.Success(new ChatMessageDto
            {
                Role = "history",
                Content = string.Empty,
                Citations = new List<ChatCitationDto>()
            });
        }

        var messages = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(item => item.ChatSessionId == session.Id && item.Role != ChatMessageRole.System)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        return new ChatServiceResult
        {
            Succeeded = true,
            Messages = messages.Select(ToDto).ToList()
        };
    }

    public async Task<ChatServiceResult> ClearAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new ChatServiceResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new ChatServiceResult { Forbidden = true };
        }

        var sessions = await _dbContext.ChatSessions
            .Where(item => item.UserId == userId && item.BookId == bookId)
            .ToListAsync(cancellationToken);

        _dbContext.ChatSessions.RemoveRange(sessions);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ChatServiceResult.Success(new ChatMessageDto
        {
            Role = "system",
            Content = "Đã xóa lịch sử chat."
        });
    }

    private async Task<Book?> LoadBookAsync(int bookId, CancellationToken cancellationToken)
    {
        return await _dbContext.Books
            .Include(book => book.ContentChunks)
            .Include(book => book.GeneratedSummaries)
            .Include(book => book.SummarySections)
            .Include(book => book.Takeaways)
            .FirstOrDefaultAsync(book => book.Id == bookId, cancellationToken);
    }

    private async Task<ChatSession> GetOrCreateSessionAsync(
        string userId,
        Book book,
        CancellationToken cancellationToken)
    {
        var session = await _dbContext.ChatSessions
            .Where(item => item.UserId == userId && item.BookId == book.Id)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session is not null)
        {
            return session;
        }

        session = new ChatSession
        {
            UserId = userId,
            BookId = book.Id,
            Title = $"LemonAI - {book.Title}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.ChatSessions.Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return session;
    }

    private async Task<List<ChatMessage>> GetRecentMessagesAsync(int sessionId, CancellationToken cancellationToken)
    {
        var messages = await _dbContext.ChatMessages
            .AsNoTracking()
            .Where(item => item.ChatSessionId == sessionId && item.Role != ChatMessageRole.System)
            .OrderByDescending(item => item.CreatedAt)
            .Take(MaxHistoryMessages)
            .ToListAsync(cancellationToken);

        return messages
            .OrderBy(item => item.CreatedAt)
            .ToList();
    }

    private async Task<string> AskGeminiAsync(
        string apiKey,
        string prompt,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        foreach (var model in await _modelRouter.OrderModelsAsync(AiModelTask.Chat, ResolveChatModels(), cancellationToken))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var answer = await AskGeminiModelAsync(apiKey, model, prompt, cancellationToken);
                await _modelRouter.ReportSuccessAsync(AiModelTask.Chat, model, stopwatch.Elapsed, cancellationToken);
                return answer;
            }
            catch (Exception exception) when (ShouldTryNextModel(exception))
            {
                await _modelRouter.ReportFailureAsync(AiModelTask.Chat, model, exception, stopwatch.Elapsed, cancellationToken);
                lastException = exception;
            }
        }

        throw lastException ?? new InvalidOperationException("LemonAI chưa trả lời được lúc này. Bạn thử lại sau một chút nhé.");
    }

    private async Task<string> AskGeminiModelAsync(
        string apiKey,
        string model,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(BuildEndpoint(apiKey, model), new
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
                temperature = 0.35,
                maxOutputTokens = Math.Clamp(_options.Gemini.MaxOutputTokens, 800, 1800)
            }
        }, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildGeminiErrorMessage((int)response.StatusCode, response.ReasonPhrase, body));
        }

        return ExtractOutputText(body);
    }

    private IReadOnlyList<string> ResolveChatModels()
    {
        var models = new List<string>();
        models.AddRange(_options.Gemini.ChatModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim()));

        if (!string.IsNullOrWhiteSpace(_options.Gemini.ChatModel))
        {
            models.Add(_options.Gemini.ChatModel.Trim());
        }

        if (!string.IsNullOrWhiteSpace(_options.Gemini.Model))
        {
            models.Add(_options.Gemini.Model.Trim());
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<ChatContextItem> SelectRelevantContext(Book book, string question)
    {
        var terms = ExtractSearchTerms(question).ToList();
        var requestedChapterNumber = ExtractRequestedNumber(question, "chuong", "chapter");
        if (requestedChapterNumber.HasValue)
        {
            var chapterItems = SelectChapterContext(book, requestedChapterNumber.Value);
            if (chapterItems.Count > 0)
            {
                return LimitContextSize(chapterItems);
            }
        }

        var requestedChunkNumber = ExtractRequestedNumber(question, "doan", "chunk");
        if (requestedChunkNumber.HasValue)
        {
            var chunkItemsByNumber = SelectChunkContext(book, requestedChunkNumber.Value);
            if (chunkItemsByNumber.Count > 0)
            {
                return LimitContextSize(chunkItemsByNumber);
            }
        }

        var chunkItems = book.ContentChunks
            .OrderBy(chunk => chunk.ChunkIndex)
            .Select(chunk => BuildChunkContextItem(book, chunk, Score(chunk.Content, terms)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ChunkIndex)
            .Take(MaxContextItems)
            .ToList();

        if (chunkItems.Count > 0 && chunkItems.Any(item => item.Score > 0))
        {
            return LimitContextSize(chunkItems);
        }

        var fallbackItems = new List<ChatContextItem>();
        fallbackItems.AddRange(book.GeneratedSummaries.Select(summary => new ChatContextItem(
            null,
            0,
            "Tóm tắt tổng quan",
            StripAndTrim($"{summary.ShortSummary}\n{summary.LongSummary}"),
            Score($"{summary.ShortSummary} {summary.LongSummary}", terms) + 1)));

        fallbackItems.AddRange(book.SummarySections.Select(section => new ChatContextItem(
            null,
            section.SortOrder,
            BuildSectionLabel(section) ?? section.Title,
            StripAndTrim(section.ContentHtml),
            Score($"{section.Title} {section.ContentHtml}", terms))));

        if (book.Takeaways.Count > 0)
        {
            var takeaways = string.Join("\n", book.Takeaways.OrderBy(item => item.SortOrder).Select(item => $"- {item.Content}"));
            fallbackItems.Add(new ChatContextItem(
                null,
                0,
                "Ý chính cần nhớ",
                takeaways,
                Score(takeaways, terms) + 1));
        }

        return LimitContextSize(fallbackItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ChunkIndex)
            .Take(MaxContextItems)
            .ToList());
    }

    private static List<ChatContextItem> SelectChapterContext(Book book, int chapterNumber)
    {
        var sections = book.SummarySections
            .Where(section =>
                section.SectionType == SummarySectionType.Chapter &&
                ((section.ChapterNumber ?? section.SortOrder) == chapterNumber))
            .OrderBy(section => section.SortOrder)
            .ToList();

        var items = sections
            .Select(section => new ChatContextItem(
                null,
                section.SortOrder,
                BuildSectionLabel(section) ?? section.Title,
                StripAndTrim(section.ContentHtml),
                100))
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .ToList();

        var sectionIds = sections.Select(section => section.Id).ToHashSet();
        var matchingChunks = book.ContentChunks
            .Where(chunk =>
                chunk.SummarySectionId.HasValue && sectionIds.Contains(chunk.SummarySectionId.Value) ||
                chunk.ChunkIndex == chapterNumber - 1)
            .OrderBy(chunk => chunk.ChunkIndex)
            .Take(2)
            .Select(chunk => new ChatContextItem(
                chunk.Id,
                chunk.ChunkIndex,
                BuildSectionLabel(ResolveChunkSection(book, chunk)) ?? $"Chương {chapterNumber}",
                StripAndTrim(chunk.Content),
                90))
            .Where(item => !string.IsNullOrWhiteSpace(item.Content));

        items.AddRange(matchingChunks);
        return items
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.ChunkIndex)
            .Take(MaxContextItems)
            .ToList();
    }

    private static List<ChatContextItem> SelectChunkContext(Book book, int chunkNumber)
    {
        return book.ContentChunks
            .Where(chunk => chunk.ChunkIndex == chunkNumber - 1)
            .OrderBy(chunk => chunk.ChunkIndex)
            .Select(chunk => BuildChunkContextItem(book, chunk, 100))
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .Take(MaxContextItems)
            .ToList();
    }

    private static ChatContextItem BuildChunkContextItem(Book book, BookContentChunk chunk, int score)
    {
        var section = ResolveChunkSection(book, chunk);
        var label = BuildSectionLabel(section) ?? "Phần nội dung liên quan";

        return new ChatContextItem(
            chunk.Id,
            chunk.ChunkIndex,
            label,
            StripAndTrim(chunk.Content),
            score);
    }

    private static BookSummarySection? ResolveChunkSection(Book book, BookContentChunk chunk)
    {
        if (chunk.SummarySectionId.HasValue)
        {
            var linkedSection = book.SummarySections.FirstOrDefault(section => section.Id == chunk.SummarySectionId.Value);
            if (linkedSection is not null)
            {
                return linkedSection;
            }
        }

        var chapters = book.SummarySections
            .Where(section => section.SectionType == SummarySectionType.Chapter)
            .OrderBy(section => section.SortOrder)
            .ToList();

        if (chapters.Count == 0)
        {
            return null;
        }

        var terms = ExtractSearchTerms(chunk.Content).ToList();
        var bestMatch = chapters
            .Select(section => new
            {
                Section = section,
                Score = Score($"{section.Title} {StripAndTrim(section.ContentHtml)}", terms)
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => Math.Abs((item.Section.ChapterNumber ?? item.Section.SortOrder) - (chunk.ChunkIndex + 1)))
            .FirstOrDefault();

        if (bestMatch is { Score: > 0 })
        {
            return bestMatch.Section;
        }

        var chunksCount = Math.Max(1, book.ContentChunks.Count);
        var chapterIndex = Math.Clamp(chunk.ChunkIndex * chapters.Count / chunksCount, 0, chapters.Count - 1);
        return chapters[chapterIndex];
    }

    private static string? BuildSectionLabel(BookSummarySection? section)
    {
        if (section is null)
        {
            return null;
        }

        if (section.SectionType == SummarySectionType.Chapter)
        {
            var chapterNumber = section.ChapterNumber ?? section.SortOrder;
            return $"Chương {chapterNumber}: {section.Title}";
        }

        return section.SectionType switch
        {
            SummarySectionType.Overview => "Tổng quan",
            SummarySectionType.KeyIdea => $"Ý chính: {section.Title}",
            SummarySectionType.Term => $"Thuật ngữ: {section.Title}",
            SummarySectionType.ActionableInsight => $"Ứng dụng: {section.Title}",
            _ => section.Title
        };
    }

    private static List<ChatContextItem> LimitContextSize(List<ChatContextItem> items)
    {
        var total = 0;
        var selected = new List<ChatContextItem>();

        foreach (var item in items)
        {
            if (total >= MaxContextCharacters)
            {
                break;
            }

            var remaining = MaxContextCharacters - total;
            var content = TrimToLength(item.Content, remaining);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            selected.Add(item with { Content = content });
            total += content.Length;
        }

        return selected;
    }

    private string BuildPrompt(
        Book book,
        string question,
        IReadOnlyList<ChatContextItem> contextItems,
        IReadOnlyList<ChatMessage> history)
    {
        var context = contextItems.Count == 0
            ? "Không có đoạn nội dung liên quan được tìm thấy."
            : string.Join("\n\n", contextItems.Select(item => $"[{item.Label}]\n{item.Content}"));

        var historyText = history.Count == 0
            ? "Chưa có lịch sử chat."
            : string.Join("\n", history.Select(item => $"{MapRole(item.Role)}: {SanitizeHistoryContent(item.Content)}"));

        return $$"""
        Bạn là LemonAI, trợ lý đọc sách của LemonInk.

        Hãy trả lời bằng tiếng Việt, thân thiện, rõ ý, dựa chủ yếu trên phần CONTEXT của sách bên dưới.
        Nếu context không đủ để kết luận, hãy nói ngắn gọn rằng bạn chưa thấy đủ thông tin trong sách thay vì bịa thêm.
        Chỉ được dùng markdown in đậm bằng **...** cho cụm từ thật quan trọng. Không dùng dấu #, code block hoặc bảng.
        Không nhắc các nhãn kỹ thuật như [Đoạn 1], [Đoạn 2] hay CONTEXT trong câu trả lời cho người đọc.
        Không gọi phần nội dung là "đoạn N" trong câu trả lời. Nếu người đọc hỏi "đoạn N", hãy quy đổi sang tên chương/phần gần nhất, ví dụ "phần bạn hỏi nằm trong Chương 2: ...".
        Nếu lịch sử chat cũ có nhắc "Đoạn N", hãy xem đó là nhãn cũ không thân thiện và ưu tiên tên chương/phần trong CONTEXT hiện tại.
        Nếu người đọc hỏi một chương cụ thể và context có nhãn "Chương N", hãy trả lời trực tiếp chương đó nói về gì.
        Không thêm phần nguồn/citation/trích dẫn ở cuối câu trả lời. Không tạo heading "Nguồn", "Sources", "Citation" hoặc "Phần nội dung liên quan".

        SÁCH:
        - Tên: {{book.Title}}
        - Tác giả: {{book.AuthorName}}
        - Thể loại: {{book.Category}}

        LỊCH SỬ GẦN ĐÂY:
        {{historyText}}

        CONTEXT:
        {{context}}

        CÂU HỎI CỦA NGƯỜI ĐỌC:
        {{question}}
        """;
    }

    private string? ResolveApiKey()
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

    private static bool ShouldTryNextModel(Exception exception)
    {
        return exception is InvalidOperationException ||
            exception is TaskCanceledException ||
            exception is TimeoutException ||
            exception is HttpRequestException;
    }

    private static bool ShouldUseLocalFallback(Exception exception)
    {
        if (exception is InvalidOperationException ||
            exception is TaskCanceledException ||
            exception is TimeoutException ||
            exception is HttpRequestException)
        {
            return true;
        }

        return false;
    }

    private static string BuildLocalFallbackAnswer(
        Book book,
        string question,
        IReadOnlyList<ChatContextItem> contextItems)
    {
        var selected = contextItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .Take(3)
            .ToList();

        if (selected.Count == 0)
        {
            return $"Mình chưa thấy đủ dữ liệu trong \"{book.Title}\" để trả lời chắc chắn câu này. Bạn thử hỏi theo chương, ý chính hoặc một khái niệm cụ thể trong sách nhé.";
        }

        var requestedChapter = ExtractRequestedNumber(question, "chuong", "chapter");
        if (requestedChapter.HasValue)
        {
            var chapter = selected.FirstOrDefault(item =>
                item.Label.Contains($"Chương {requestedChapter.Value}", StringComparison.OrdinalIgnoreCase));

            if (chapter is not null && chapter.Content.Length > 0)
            {
                return $"Chương {requestedChapter.Value} của \"{book.Title}\" nói về {LowercaseFirst(SummarizeContext(chapter.Content))}";
            }
        }

        var requestedChunk = ExtractRequestedNumber(question, "doan", "chunk");
        if (requestedChunk.HasValue)
        {
            var context = selected[0];
            return $"Phần bạn gọi là đoạn {requestedChunk.Value} tương ứng gần nhất với \"{context.Label}\" trong \"{book.Title}\". Nội dung chính là {LowercaseFirst(SummarizeContext(context.Content))}";
        }

        if (LooksLikeSummaryQuestion(question))
        {
            var points = selected
                .Select(item => SummarizeContext(item.Content))
                .Where(point => !string.IsNullOrWhiteSpace(point))
                .Take(3)
                .ToList();

            if (points.Count > 0)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"Mình tóm tắt nhanh \"{book.Title}\" theo nội dung đang có trong sách:");
                for (var index = 0; index < points.Count; index++)
                {
                    builder.AppendLine($"{index + 1}. {points[index]}");
                }

                return builder.ToString().Trim();
            }
        }

        var bestContext = selected[0];
        return $"Trong \"{book.Title}\", {LowercaseFirst(SummarizeContext(bestContext.Content))}";
    }

    private static bool LooksLikeSummaryQuestion(string question)
    {
        var normalized = RemoveDiacritics(question).ToLowerInvariant();
        return normalized.Contains("tom tat", StringComparison.Ordinal) ||
            normalized.Contains("noi ve gi", StringComparison.Ordinal) ||
            normalized.Contains("y chinh", StringComparison.Ordinal) ||
            normalized.Contains("main idea", StringComparison.Ordinal) ||
            normalized.Contains("summary", StringComparison.Ordinal);
    }

    private static string SummarizeContext(string content)
    {
        var clean = CollapseText(content);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return string.Empty;
        }

        var sentences = System.Text.RegularExpressions.Regex
            .Split(clean, @"(?<=[.!?。])\s+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .Take(2)
            .ToList();

        var summary = sentences.Count == 0
            ? clean
            : string.Join(" ", sentences);

        return TrimToLength(summary, 520).TrimEnd('.', ',', ';', ':') + ".";
    }

    private static string CollapseText(string value)
    {
        return System.Text.RegularExpressions.Regex
            .Replace(value ?? string.Empty, @"\s+", " ")
            .Trim();
    }

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return char.ToLower(value[0], CultureInfo.CurrentCulture) + value[1..];
    }

    private static bool CanRead(Book book, string userId)
    {
        return book.Visibility == BookVisibility.Public ||
            (book.SourceType == BookSourceType.UserUpload && book.OwnerUserId == userId);
    }

    private static string NormalizeQuestion(string message)
    {
        var question = (message ?? string.Empty).Trim();
        return question.Length <= MaxQuestionLength ? question : question[..MaxQuestionLength];
    }

    private static IEnumerable<string> ExtractSearchTerms(string text)
    {
        var normalized = RemoveDiacritics(text).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return builder
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 2 && !StopWords.Contains(term))
            .Distinct()
            .Take(12);
    }

    private static int? ExtractRequestedNumber(string text, params string[] normalizedKeywords)
    {
        var normalized = RemoveDiacritics(text).ToLowerInvariant();

        foreach (var keyword in normalizedKeywords)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                normalized,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(keyword)}\s*(\d+)\b");

            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var number) &&
                number > 0)
            {
                return number;
            }
        }

        return null;
    }

    private static int Score(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return 1;
        }

        var normalized = RemoveDiacritics(text).ToLowerInvariant();
        var score = 0;

        foreach (var term in terms)
        {
            var index = normalized.IndexOf(term, StringComparison.Ordinal);
            while (index >= 0)
            {
                score += 1;
                index = normalized.IndexOf(term, index + term.Length, StringComparison.Ordinal);
            }
        }

        return score;
    }

    private static string StripAndTrim(string value)
    {
        var withoutTags = System.Text.RegularExpressions.Regex
            .Replace(value ?? string.Empty, "<.*?>", " ")
            .Replace("&nbsp;", " ")
            .Trim();

        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string TrimToLength(string value, int maxLength)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        var clean = value.Trim();
        return clean.Length <= maxLength ? clean : $"{clean[..maxLength].Trim()}...";
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character == 'đ' ? 'd' : character == 'Đ' ? 'D' : character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("candidates", out var candidatesElement) ||
            candidatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("LemonAI chưa nhận được câu trả lời hợp lệ. Bạn thử lại sau một chút nhé.");
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
            throw new InvalidOperationException("LemonAI chưa trả về nội dung chat. Bạn thử lại sau một chút nhé.");
        }

        return text;
    }

    private static string BuildGeminiErrorMessage(int statusCode, string? reasonPhrase, string responseBody)
    {
        if (statusCode == 429)
        {
            return "LemonAI đang có nhiều lượt hỏi cùng lúc. Bạn thử lại sau một chút nhé.";
        }

        if (statusCode == 503)
        {
            return "LemonAI đang hơi quá tải nên chưa trả lời được. Bạn thử lại sau vài phút nhé.";
        }

        return "LemonAI chưa trả lời được lúc này. Bạn thử lại sau một chút nhé.";
    }

    private static string MapRole(ChatMessageRole role)
    {
        return role == ChatMessageRole.User ? "Người đọc" : "LemonAI";
    }

    private static string SanitizeHistoryContent(string content)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            content ?? string.Empty,
            @"\bĐoạn\s+\d+\b",
            "phần nội dung cũ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string SanitizeAssistantAnswer(string content)
    {
        var clean = (content ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            return clean;
        }

        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"(?ims)\n{0,2}\s*(?:#{1,6}\s*)?(?:nguồn|sources?|citations?|trích dẫn|phan noi dung lien quan|phần nội dung liên quan)\s*:?\s*(?:\r?\n|$).*$",
            string.Empty);

        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"(?im)^\s*(?:nguồn|sources?|citations?|trích dẫn)\s*:\s*.+$",
            string.Empty);

        return string.IsNullOrWhiteSpace(clean) ? (content ?? string.Empty).Trim() : clean.Trim();
    }

    private static ChatMessageDto ToDto(ChatMessage message)
    {
        return new ChatMessageDto
        {
            Id = message.Id,
            Role = message.Role == ChatMessageRole.User ? "user" : "assistant",
            Content = message.Content,
            CreatedAt = message.CreatedAt,
            Citations = new List<ChatCitationDto>()
        };
    }

    private static ChatMessageDto ToDto(ChatMessage message, IReadOnlyList<ChatContextItem> contextItems)
    {
        return new ChatMessageDto
        {
            Id = message.Id,
            Role = "assistant",
            Content = message.Content,
            CreatedAt = message.CreatedAt,
            Citations = new List<ChatCitationDto>()
        };
    }

    private sealed record ChatContextItem(
        int? ChunkId,
        int ChunkIndex,
        string Label,
        string Content,
        int Score)
    {
        public string Quote => Content;
    }
}
