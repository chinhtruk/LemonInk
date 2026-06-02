using System.Text;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Audio;
using ZenRead.Services.Books;
using ZenRead.Services.Email;
using ZenRead.Services.Summarization;

namespace ZenRead.Services.Processing;

public class ProcessingJobService : IProcessingJobService
{
    private const int MaxChunkWords = 720;
    private const int ChunkOverlapWords = 80;
    private const int MaxOcrTransientRetries = 30;
    private const int MaxSummaryProviderRetries = 24;
    private const int MaxAudioRateLimitRetries = 50;
    private static readonly TimeSpan AbandonedRunningJobAge = TimeSpan.FromMinutes(10);

    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly ITextExtractionService _textExtractionService;
    private readonly IBookSummarizationService _summarizationService;
    private readonly IAudioGenerationService _audioGenerationService;
    private readonly IEmailNotificationService _emailNotifications;
    private readonly ILogger<ProcessingJobService> _logger;

    public ProcessingJobService(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        ITextExtractionService textExtractionService,
        IBookSummarizationService summarizationService,
        IAudioGenerationService audioGenerationService,
        IEmailNotificationService emailNotifications,
        ILogger<ProcessingJobService> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _textExtractionService = textExtractionService;
        _summarizationService = summarizationService;
        _audioGenerationService = audioGenerationService;
        _emailNotifications = emailNotifications;
        _logger = logger;
    }

    public async Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken)
    {
        await RequeueAbandonedRunningJobsAsync(cancellationToken);
        await EnsureQueuedSummaryJobsAsync(cancellationToken);
        await EnsureQueuedAudioJobsAsync(cancellationToken);

        var job = await GetNextQueuedJobAsync(cancellationToken);

        if (job is null)
        {
            return false;
        }

        return job.Type switch
        {
            ProcessingJobType.ExtractText => await ProcessExtractTextJobAsync(job, cancellationToken),
            ProcessingJobType.SummarizeBook => await ProcessSummarizeJobAsync(job, cancellationToken),
            ProcessingJobType.GenerateAudio => await ProcessGenerateAudioJobAsync(job, cancellationToken),
            _ => await SkipUnsupportedJobAsync(job, cancellationToken)
        };
    }

    private async Task<ProcessingJob?> GetNextQueuedJobAsync(CancellationToken cancellationToken)
    {
        var supportedJobTypes = new[] { ProcessingJobType.ExtractText, ProcessingJobType.SummarizeBook, ProcessingJobType.GenerateAudio };
        var now = DateTime.UtcNow;

        return await _dbContext.ProcessingJobs
            .Include(item => item.Book)
                .ThenInclude(book => book.Files)
            .Include(item => item.Book)
                .ThenInclude(book => book.ContentChunks)
            .Include(item => item.Book)
                .ThenInclude(book => book.GeneratedSummaries)
            .Include(item => item.Book)
                .ThenInclude(book => book.SummarySections)
            .Include(item => item.Book)
                .ThenInclude(book => book.Takeaways)
            .Include(item => item.Book)
                .ThenInclude(book => book.Audios)
            .Include(item => item.Book)
                .ThenInclude(book => book.OwnerUser)
            .Where(item =>
                supportedJobTypes.Contains(item.Type) &&
                item.Status == ProcessingJobStatus.Queued &&
                (!item.NextRunAt.HasValue || item.NextRunAt <= now))
            .OrderBy(item => item.NextRunAt ?? item.CreatedAt)
            .ThenBy(item => item.UpdatedAt)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> ProcessExtractTextJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await MarkJobRunningAsync(job, "Worker đã nhận job trích xuất nội dung.", cancellationToken);

        try
        {
            var book = job.Book;
            var file = book.Files
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();

            if (file is null)
            {
                throw new InvalidOperationException("Sách upload chưa có file nguồn.");
            }

            await UpdateProgressAsync(job, BookProcessingStatus.ExtractingText, 20, "Đang trích xuất nội dung từ file.", cancellationToken);

            var extractedText = await _textExtractionService.ExtractAsync(file, cancellationToken);
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                throw new InvalidOperationException("Không tìm thấy nội dung chữ trong file upload.");
            }

            PipelineQualityValidator.EnsureExtractedTextIsUsable(extractedText);

            var extractedTextPath = await SaveExtractedTextAsync(book.OwnerUserId, book.Id, extractedText, cancellationToken);
            file.ExtractedTextPath = extractedTextPath;
            file.UploadStatus = BookFileUploadStatus.Extracted;

            await UpdateProgressAsync(job, BookProcessingStatus.ExtractingText, 55, "Đã trích xuất nội dung, đang chia thành các đoạn đọc.", cancellationToken);

            var chunks = BuildChunks(book.Id, extractedText);
            if (chunks.Count == 0)
            {
                throw new InvalidOperationException("Không tạo được nội dung chunk từ file upload.");
            }

            await _dbContext.BookContentChunks
                .Where(chunk => chunk.BookId == book.Id)
                .ExecuteDeleteAsync(cancellationToken);

            _dbContext.BookContentChunks.AddRange(chunks);

            book.ProcessingStatus = BookProcessingStatus.Extracted;
            book.IsSummaryReady = false;
            book.IsAudioReady = false;
            book.ReadingTimeMinutes = EstimateReadingMinutes(extractedText);
            book.UpdatedAt = DateTime.UtcNow;

            job.Status = ProcessingJobStatus.Succeeded;
            job.ProgressPercent = 100;
            job.CurrentStep = $"Đã trích xuất và chia thành {chunks.Count} đoạn nội dung.";
            job.NextRunAt = null;
            job.FinishedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

            _dbContext.BookProcessingQualityReports.Add(BuildExtractionQualityReport(book, job, extractedText, chunks));

            if (!await HasActiveSummaryJobAsync(book.Id, cancellationToken))
            {
                _dbContext.ProcessingJobs.Add(new ProcessingJob
                {
                    BookId = book.Id,
                    UserId = book.OwnerUserId,
                    Type = ProcessingJobType.SummarizeBook,
                    Status = ProcessingJobStatus.Queued,
                    ProgressPercent = 0,
                    CurrentStep = "Đã sẵn sàng tạo bản tóm tắt.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            if (exception is OcrProviderTemporaryException temporaryException &&
                job.RetryCount < MaxOcrTransientRetries)
            {
                _logger.LogWarning(
                    temporaryException,
                    "OCR provider is temporarily unavailable for extract job {JobId}. Requeueing.",
                    job.Id);
                await RequeueExtractJobAfterTemporaryOcrAsync(job, temporaryException, cancellationToken);
                return true;
            }

            _logger.LogError(exception, "Failed to process extract job {JobId}", job.Id);
            await MarkJobFailedAsync(job, exception.Message, cancellationToken);
            return true;
        }
    }

    private async Task<bool> ProcessSummarizeJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await MarkJobRunningAsync(job, "Worker đã nhận job tạo tóm tắt.", cancellationToken);

        try
        {
            var book = job.Book;
            var chunks = book.ContentChunks
                .OrderBy(chunk => chunk.ChunkIndex)
                .ToList();

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException("Chưa có nội dung chunk để tạo tóm tắt.");
            }

            await UpdateProgressAsync(job, BookProcessingStatus.Summarizing, 25, "Đang đọc các đoạn nội dung đã trích xuất.", cancellationToken);

            var result = await _summarizationService.SummarizeAsync(book, chunks, cancellationToken);
            PipelineQualityValidator.EnsureSummaryIsComplete(
                result,
                chunks.Sum(chunk => chunk.Content.Length));

            await UpdateProgressAsync(job, BookProcessingStatus.Summarizing, 70, "Đang lưu bản tóm tắt, ý chính và chương.", cancellationToken);
            var introduction = BuildBriefIntroduction(result.ShortSummary);

            await _dbContext.GeneratedBookSummaries
                .Where(summary => summary.BookId == book.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await _dbContext.BookSummarySections
                .Where(section => section.BookId == book.Id)
                .ExecuteDeleteAsync(cancellationToken);
            await _dbContext.BookTakeaways
                .Where(takeaway => takeaway.BookId == book.Id)
                .ExecuteDeleteAsync(cancellationToken);

            _dbContext.GeneratedBookSummaries.Add(new GeneratedBookSummary
            {
                BookId = book.Id,
                ShortSummary = introduction,
                LongSummary = result.LongSummary,
                GeneratedBy = result.GeneratedBy,
                GeneratedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            _dbContext.BookSummarySections.Add(new BookSummarySection
            {
                BookId = book.Id,
                SectionType = SummarySectionType.Overview,
                Title = "Tổng quan",
                ContentHtml = result.LongSummary,
                ReadingTimeMinutes = Math.Max(1, result.LongSummary.Length / 1200),
                SortOrder = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            foreach (var chapter in result.Chapters)
            {
                _dbContext.BookSummarySections.Add(new BookSummarySection
                {
                    BookId = book.Id,
                    SectionType = SummarySectionType.Chapter,
                    ChapterNumber = chapter.Number,
                    Title = chapter.Title,
                    ContentHtml = chapter.ContentHtml,
                    ReadingTimeMinutes = chapter.ReadingTimeMinutes,
                    SortOrder = chapter.Number,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            for (var index = 0; index < result.KeyTakeaways.Count; index++)
            {
                _dbContext.BookTakeaways.Add(new BookTakeaway
                {
                    BookId = book.Id,
                    Content = result.KeyTakeaways[index],
                    SortOrder = index + 1
                });
            }

            book.ProcessingStatus = BookProcessingStatus.SummaryReady;
            book.IsSummaryReady = true;
            book.IsAudioReady = false;
            book.Introduction = introduction;
            book.FailedReason = null;
            book.UpdatedAt = DateTime.UtcNow;

            job.Status = ProcessingJobStatus.Succeeded;
            job.ProgressPercent = 100;
            job.CurrentStep = result.GeneratedBy.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                result.GeneratedBy.Contains("draft", StringComparison.OrdinalIgnoreCase)
                    ? "Đã tạo bản tóm tắt nháp từ nội dung đã trích xuất."
                    : "Đã tạo bản tóm tắt AI từ nội dung đã trích xuất.";
            job.ErrorMessage = null;
            job.NextRunAt = null;
            job.FinishedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

            _dbContext.BookProcessingQualityReports.Add(BuildSummaryQualityReport(book, job, result, chunks));

            if (_audioGenerationService.IsConfigured &&
                !await HasActiveAudioJobAsync(book.Id, cancellationToken))
            {
                _dbContext.ProcessingJobs.Add(new ProcessingJob
                {
                    BookId = book.Id,
                    UserId = book.OwnerUserId,
                    Type = ProcessingJobType.GenerateAudio,
                    Status = ProcessingJobStatus.Queued,
                    ProgressPercent = 0,
                    CurrentStep = "Đã sẵn sàng tạo audio từ bản tóm tắt.",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await _dbContext.BookSummaryPassCheckpoints
                .Where(checkpoint => checkpoint.BookId == book.Id)
                .ExecuteDeleteAsync(cancellationToken);
            if (!_audioGenerationService.IsConfigured)
            {
                await NotifyBookReadyAsync(book, cancellationToken);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (IsGeminiRetryableProviderError(exception) &&
                job.RetryCount < MaxSummaryProviderRetries)
            {
                _logger.LogWarning(
                    exception,
                    "Summary provider is temporarily unavailable for job {JobId}. Requeueing.",
                    job.Id);
                await RequeueSummaryJobAfterTransientProviderAsync(job, exception.Message, cancellationToken);
                return true;
            }

            _logger.LogError(exception, "Failed to process summarize job {JobId}", job.Id);
            await MarkSummaryJobFailedAsync(job, exception.Message, cancellationToken);
            return true;
        }
    }

    private static string BuildBriefIntroduction(string value)
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var sentences = Regex.Matches(normalized, @"[^.!?]+[.!?]?")
            .Select(match => match.Value.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Take(3)
            .ToList();
        var introduction = sentences.Count == 0
            ? normalized
            : string.Join(" ", sentences);
        var words = introduction.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= 55)
        {
            return introduction;
        }

        return $"{string.Join(" ", words.Take(55)).TrimEnd('.', ',', ';', ':')}...";
    }

    private async Task<bool> ProcessGenerateAudioJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        await MarkJobRunningAsync(job, "Worker đã nhận job tạo audio.", cancellationToken);

        try
        {
            var book = job.Book;
            var summary = book.GeneratedSummaries
                .OrderByDescending(item => item.GeneratedAt)
                .FirstOrDefault();

            if (summary is null)
            {
                throw new InvalidOperationException("Chưa có bản tóm tắt để tạo audio.");
            }

            await UpdateProgressAsync(job, BookProcessingStatus.GeneratingAudio, 25, "Đang chuẩn bị kịch bản audio.", cancellationToken);

            await UpdateProgressAsync(job, BookProcessingStatus.GeneratingAudio, 45, "Đang gọi TTS provider để tạo audio.", cancellationToken);

            var result = await _audioGenerationService.GenerateAsync(
                book,
                summary,
                book.SummarySections.OrderBy(section => section.SortOrder).ToList(),
                book.Takeaways.OrderBy(takeaway => takeaway.SortOrder).ToList(),
                async (progress, token) =>
                {
                    var percent = 45 + (int)Math.Round(
                        35d * progress.CompletedSegments / Math.Max(1, progress.TotalSegments));
                    var resumeNote = progress.LoadedFromCheckpoint
                        ? " Đã khôi phục đoạn đã tạo trước đó."
                        : string.Empty;

                    await UpdateProgressAsync(
                        job,
                        BookProcessingStatus.GeneratingAudio,
                        percent,
                        $"Đang tạo audio: hoàn tất đoạn {progress.CompletedSegments}/{progress.TotalSegments}.{resumeNote}",
                        token);
                },
                cancellationToken);

            await UpdateProgressAsync(job, BookProcessingStatus.GeneratingAudio, 85, "Đang lưu file audio và metadata.", cancellationToken);

            _dbContext.BookAudios.Add(new BookAudio
            {
                BookId = book.Id,
                AudioUrl = result.StoredAudioPath,
                DurationSeconds = result.DurationSeconds,
                VoiceName = result.VoiceName,
                Provider = result.Provider,
                Status = AudioStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            book.ProcessingStatus = BookProcessingStatus.Ready;
            book.IsAudioReady = true;
            book.FailedReason = null;
            book.AudioDurationSeconds = result.DurationSeconds;
            ReadingTimeCalculator.SyncWithAudio(
                book,
                book.SummarySections.OrderBy(section => section.SortOrder).ToList(),
                result.DurationSeconds);
            book.UpdatedAt = DateTime.UtcNow;

            job.Status = ProcessingJobStatus.Succeeded;
            job.ProgressPercent = 100;
            job.CurrentStep = $"Đã tạo audio đầy đủ từ bản tóm tắt ({result.SegmentCount} đoạn, đã kiểm tra thời lượng).";
            job.ErrorMessage = null;
            job.NextRunAt = null;
            job.FinishedAt = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;

            _dbContext.BookProcessingQualityReports.Add(BuildAudioQualityReport(
                book,
                job,
                result,
                summary,
                book.SummarySections.OrderBy(section => section.SortOrder).ToList(),
                book.Takeaways.OrderBy(takeaway => takeaway.SortOrder).ToList()));

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process audio job {JobId}", job.Id);
            var failureMessage = BuildAudioFailureMessage(exception);

            if (IsGeminiRetryableProviderError(exception) &&
                job.RetryCount < MaxAudioRateLimitRetries)
            {
                await RequeueAudioJobAfterTransientProviderAsync(job, exception.Message, cancellationToken);
                return true;
            }

            await MarkAudioJobFailedAsync(job, failureMessage, cancellationToken);
            return true;
        }
    }

    private static BookProcessingQualityReport BuildExtractionQualityReport(
        Book book,
        ProcessingJob job,
        string extractedText,
        IReadOnlyCollection<BookContentChunk> chunks)
    {
        var wordCount = CountWords(extractedText);
        var estimatedPages = EstimatePageCount(wordCount);
        var warnings = new List<string>();

        if (chunks.Count == 0)
        {
            warnings.Add("Không tạo được chunk nội dung từ file.");
        }

        if (estimatedPages >= 80 && chunks.Count < Math.Max(8, estimatedPages / 8))
        {
            warnings.Add("Sách dài nhưng số chunk khá ít, cần kiểm tra lại bước tách nội dung.");
        }

        if (wordCount >= 50_000 && chunks.Count < 40)
        {
            warnings.Add("Nội dung trích xuất lớn nhưng chưa chia đủ nhiều chunk để tóm tắt sâu.");
        }

        return new BookProcessingQualityReport
        {
            BookId = book.Id,
            ProcessingJobId = job.Id,
            Stage = ProcessingQualityStage.ExtractText,
            Status = ResolveQualityStatus(warnings),
            EstimatedPageCount = estimatedPages,
            SourceChunkCount = chunks.Count,
            ExtractedWordCount = wordCount,
            SummaryCoveragePercent = chunks.Count > 0 ? 100 : 0,
            WarningsJson = SerializeWarnings(warnings),
            Notes = chunks.Count > 0
                ? $"Đã đọc khoảng {estimatedPages} trang và chia thành {chunks.Count} chunk."
                : "Không có chunk nội dung để xử lý tiếp.",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static BookProcessingQualityReport BuildSummaryQualityReport(
        Book book,
        ProcessingJob job,
        BookSummarizationResult result,
        IReadOnlyCollection<BookContentChunk> chunks)
    {
        var sourceWords = chunks.Sum(chunk => CountWords(chunk.Content));
        var estimatedPages = EstimatePageCount(sourceWords);
        var usableChapters = result.Chapters
            .Where(chapter =>
                !string.IsNullOrWhiteSpace(chapter.Title) &&
                CountWords(chapter.ContentHtml) >= 15)
            .ToList();
        var expectedChapterCount = result.ExpectedSourceChapters.Count > 0
            ? result.ExpectedSourceChapters.Count
            : usableChapters.Count;
        var coveredChapterCount = result.ExpectedSourceChapters.Count > 0
            ? result.ExpectedSourceChapters.Count(chapter =>
                usableChapters.Any(item => item.Number == chapter.Number))
            : usableChapters.Count;
        var missingChapterCount = Math.Max(0, expectedChapterCount - coveredChapterCount);
        var summaryWords = CountWords(result.LongSummary) +
            usableChapters.Sum(chapter => CountWords(chapter.ContentHtml)) +
            result.KeyTakeaways.Sum(takeaway => CountWords(takeaway));
        var coverage = expectedChapterCount > 0
            ? Math.Round(coveredChapterCount * 100m / expectedChapterCount, 2)
            : usableChapters.Count > 0 ? 100m : 0m;
        var warnings = new List<string>();

        if (missingChapterCount > 0)
        {
            warnings.Add($"Bản tóm tắt còn thiếu {missingChapterCount} chương so với mục lục phát hiện.");
        }

        if (sourceWords >= 60_000 && summaryWords < 7_000)
        {
            warnings.Add("Sách dài nhưng tổng dung lượng tóm tắt còn thấp, có thể mất ý phụ.");
        }

        if (sourceWords >= 120_000 && usableChapters.Sum(chapter => CountWords(chapter.ContentHtml)) < 10_000)
        {
            warnings.Add("Nội dung từng chương còn ngắn so với kích thước sách.");
        }

        if (estimatedPages >= 120 && usableChapters.Count < 8)
        {
            warnings.Add("Sách dài nhưng số chương tóm tắt ít, cần kiểm tra lại phát hiện mục lục.");
        }

        return new BookProcessingQualityReport
        {
            BookId = book.Id,
            ProcessingJobId = job.Id,
            Stage = ProcessingQualityStage.SummarizeBook,
            Status = ResolveQualityStatus(warnings),
            EstimatedPageCount = estimatedPages,
            SourceChunkCount = chunks.Count,
            ExtractedWordCount = sourceWords,
            DetectedChapterCount = usableChapters.Count,
            ExpectedChapterCount = expectedChapterCount,
            CoveredChapterCount = coveredChapterCount,
            MissingChapterCount = missingChapterCount,
            SummarySectionCount = usableChapters.Count + 1,
            SummaryWordCount = summaryWords,
            SummaryCoveragePercent = coverage,
            WarningsJson = SerializeWarnings(warnings),
            Notes = $"Tóm tắt bao phủ {coveredChapterCount}/{Math.Max(1, expectedChapterCount)} chương, khoảng {summaryWords:N0} từ.",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static BookProcessingQualityReport BuildAudioQualityReport(
        Book book,
        ProcessingJob job,
        BookAudioGenerationResult result,
        GeneratedBookSummary summary,
        IReadOnlyCollection<BookSummarySection> sections,
        IReadOnlyCollection<BookTakeaway> takeaways)
    {
        var scriptText = BuildEstimatedAudioScript(book, summary, sections, takeaways);
        var scriptWords = CountWords(scriptText);
        var expectedDurationSeconds = EstimateNarrationSeconds(scriptWords);
        var audioCoverage = expectedDurationSeconds > 0
            ? Math.Round(Math.Min(999m, result.DurationSeconds * 100m / expectedDurationSeconds), 2)
            : 100m;
        var chapterSections = sections
            .Where(section => section.SectionType == SummarySectionType.Chapter)
            .ToList();
        var summaryWords = sections.Sum(section => CountWords(section.ContentHtml)) +
            takeaways.Sum(takeaway => CountWords(takeaway.Content)) +
            CountWords(summary.ShortSummary);
        var warnings = new List<string>();

        if (result.SegmentCount <= 0)
        {
            warnings.Add("Không có segment audio nào được ghi nhận.");
        }

        if (expectedDurationSeconds > 0 && result.DurationSeconds < expectedDurationSeconds * 0.7)
        {
            warnings.Add("Audio ngắn hơn đáng kể so với nội dung tóm tắt, cần kiểm tra thiếu chunk TTS.");
        }

        if (result.ScriptCharacterCount > 0 && result.ScriptCharacterCount < scriptText.Length * 0.55)
        {
            warnings.Add("Script audio thực tế ngắn hơn nhiều so với nội dung cần đọc.");
        }

        return new BookProcessingQualityReport
        {
            BookId = book.Id,
            ProcessingJobId = job.Id,
            Stage = ProcessingQualityStage.GenerateAudio,
            Status = ResolveQualityStatus(warnings),
            DetectedChapterCount = chapterSections.Count,
            ExpectedChapterCount = chapterSections.Count,
            CoveredChapterCount = chapterSections.Count,
            SummarySectionCount = sections.Count,
            SummaryWordCount = summaryWords,
            AudioDurationSeconds = result.DurationSeconds,
            ExpectedAudioDurationSeconds = expectedDurationSeconds,
            AudioSegmentCount = result.SegmentCount,
            AudioScriptCharacterCount = result.ScriptCharacterCount,
            SummaryCoveragePercent = 100,
            AudioCoveragePercent = audioCoverage,
            WarningsJson = SerializeWarnings(warnings),
            Notes = $"Audio dài {FormatDuration(result.DurationSeconds)}, ước đạt {audioCoverage:0.#}% nội dung cần đọc.",
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string BuildEstimatedAudioScript(
        Book book,
        GeneratedBookSummary summary,
        IReadOnlyCollection<BookSummarySection> sections,
        IReadOnlyCollection<BookTakeaway> takeaways)
    {
        var builder = new StringBuilder();
        builder.Append("Tóm tắt sách ");
        builder.Append(book.Title);

        if (!string.IsNullOrWhiteSpace(book.AuthorName))
        {
            builder.Append(" của tác giả ");
            builder.Append(book.AuthorName);
        }

        builder.AppendLine(".");
        builder.AppendLine(ToPlainText(summary.ShortSummary));

        foreach (var section in sections.OrderBy(section => section.SortOrder))
        {
            builder.AppendLine(section.Title);
            builder.AppendLine(ToPlainText(section.ContentHtml));
        }

        if (takeaways.Count > 0)
        {
            builder.AppendLine("Ý chính cần nhớ");
            foreach (var takeaway in takeaways.OrderBy(takeaway => takeaway.SortOrder))
            {
                builder.AppendLine(takeaway.Content);
            }
        }

        return builder.ToString();
    }

    private static ProcessingQualityStatus ResolveQualityStatus(IReadOnlyCollection<string> warnings)
    {
        return warnings.Count > 0 ? ProcessingQualityStatus.Warning : ProcessingQualityStatus.Passed;
    }

    private static string SerializeWarnings(IReadOnlyCollection<string> warnings)
    {
        return JsonSerializer.Serialize(warnings);
    }

    private static int CountWords(string? value)
    {
        return Regex.Matches(ToPlainText(value), @"[\p{L}\p{N}]+").Count;
    }

    private static string ToPlainText(string? value)
    {
        var withoutHtml = Regex.Replace(value ?? string.Empty, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutHtml);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static int EstimatePageCount(int wordCount)
    {
        return wordCount <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(wordCount / 350d));
    }

    private static int EstimateNarrationSeconds(int wordCount)
    {
        return wordCount <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(wordCount * 60d / 145d));
    }

    private static string FormatDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    private async Task RequeueAbandonedRunningJobsAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - AbandonedRunningJobAge;
        var jobs = await _dbContext.ProcessingJobs
            .Include(job => job.Book)
            .Where(job => job.Status == ProcessingJobStatus.Running && job.UpdatedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return;
        }

        foreach (var job in jobs)
        {
            job.Status = ProcessingJobStatus.Queued;
            job.ProgressPercent = 0;
            job.RetryCount += 1;
            job.NextRunAt = null;
            job.StartedAt = null;
            job.FinishedAt = null;
            job.ErrorMessage = null;
            job.CurrentStep = "Job đang chạy bị gián đoạn, LemonInk đã đưa lại vào hàng đợi.";
            job.UpdatedAt = DateTime.UtcNow;

            job.Book.ProcessingStatus = job.Type switch
            {
                ProcessingJobType.ExtractText => BookProcessingStatus.Uploaded,
                ProcessingJobType.SummarizeBook => BookProcessingStatus.Extracted,
                ProcessingJobType.GenerateAudio => BookProcessingStatus.SummaryReady,
                _ => job.Book.ProcessingStatus
            };
            job.Book.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RequeueSummaryJobAfterTransientProviderAsync(
        ProcessingJob job,
        string message,
        CancellationToken cancellationToken)
    {
        var retryAfter = ExtractRetryAfterDelay(message);
        var nextRetryCount = job.RetryCount + 1;

        job.Status = ProcessingJobStatus.Queued;
        job.ProgressPercent = Math.Max(job.ProgressPercent, 25);
        job.RetryCount = nextRetryCount;
        job.NextRunAt = DateTime.UtcNow + retryAfter;
        job.CurrentStep = $"{DescribeProviderWait("Gemini", message)}, LemonInk sẽ tự thử lại tạo tóm tắt sau khoảng {Math.Ceiling(retryAfter.TotalSeconds)} giây. Lần thử {nextRetryCount}/{MaxSummaryProviderRetries}.";
        job.ErrorMessage = message;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.UpdatedAt = DateTime.UtcNow;

        job.Book.ProcessingStatus = BookProcessingStatus.Summarizing;
        job.Book.IsSummaryReady = false;
        job.Book.FailedReason = null;
        job.Book.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RequeueAudioJobAfterTransientProviderAsync(
        ProcessingJob job,
        string message,
        CancellationToken cancellationToken)
    {
        var retryAfter = ExtractRetryAfterDelay(message);

        job.Status = ProcessingJobStatus.Queued;
        job.ProgressPercent = Math.Max(job.ProgressPercent, 45);
        job.RetryCount += 1;
        job.NextRunAt = DateTime.UtcNow + retryAfter;
        job.CurrentStep = $"{DescribeProviderWait("TTS", message)}. LemonInk sẽ thử lại sau khoảng {Math.Ceiling(retryAfter.TotalSeconds)} giây và giữ lại các đoạn audio đã tạo.";
        job.ErrorMessage = message;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.UpdatedAt = DateTime.UtcNow;

        job.Book.ProcessingStatus = BookProcessingStatus.SummaryReady;
        job.Book.IsAudioReady = job.Book.Audios.Any(audio => audio.Status == AudioStatus.Ready);
        job.Book.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task NotifyBookReadyAsync(Book book, CancellationToken cancellationToken)
    {
        try
        {
            var audioDuration = book.AudioDurationSeconds is > 0
                ? FormatAudioDuration(book.AudioDurationSeconds.Value)
                : null;
            var chapterCount = book.SummarySections.Count(section => section.SectionType == SummarySectionType.Chapter);
            await _emailNotifications.SendBookReadyAsync(book.OwnerUser, book, audioDuration, chapterCount, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send book-ready email for book {BookId}.", book.Id);
        }
    }

    private async Task NotifyBookFailedAsync(Book book, string failedStep, CancellationToken cancellationToken)
    {
        try
        {
            await _emailNotifications.SendBookProcessingFailedAsync(book.OwnerUser, book, failedStep, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send book-processing failure email for book {BookId}.", book.Id);
        }
    }

    private static string FormatAudioDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private async Task RequeueExtractJobAfterTemporaryOcrAsync(
        ProcessingJob job,
        OcrProviderTemporaryException exception,
        CancellationToken cancellationToken)
    {
        var retryAfter = exception.RetryAfter <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(75)
            : exception.RetryAfter;
        var nextRetryCount = job.RetryCount + 1;

        job.Status = ProcessingJobStatus.Queued;
        job.ProgressPercent = Math.Max(job.ProgressPercent, 25);
        job.RetryCount = nextRetryCount;
        job.NextRunAt = DateTime.UtcNow + retryAfter;
        job.CurrentStep = $"OCR cho PDF ảnh đang bận, LemonInk sẽ tự thử lại sau khoảng {Math.Ceiling(retryAfter.TotalSeconds)} giây. Lần thử {nextRetryCount}/{MaxOcrTransientRetries}.";
        job.ErrorMessage = exception.Message;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.UpdatedAt = DateTime.UtcNow;

        job.Book.ProcessingStatus = BookProcessingStatus.ExtractingText;
        job.Book.FailedReason = null;
        job.Book.UpdatedAt = DateTime.UtcNow;

        foreach (var file in job.Book.Files)
        {
            if (file.UploadStatus != BookFileUploadStatus.Extracted)
            {
                file.UploadStatus = BookFileUploadStatus.Uploaded;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsGeminiRetryableProviderError(Exception exception)
    {
        var message = exception.Message;
        return exception is TaskCanceledException ||
            exception is TimeoutException ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("504", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAudioFailureMessage(Exception exception)
    {
        var message = exception.Message;
        if (exception is TaskCanceledException ||
            exception is TimeoutException ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini TTS phản hồi quá lâu nên LemonInk đã dừng job. Bạn có thể retry tạo audio sau ít phút.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "Không tạo được audio từ bản tóm tắt."
            : message;
    }

    private static string DescribeProviderWait(string providerName, string message)
    {
        if (message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return $"{providerName} đã chạm giới hạn lượt gọi hiện tại";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return $"{providerName} đang bị giới hạn tốc độ";
        }

        if (message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("high demand", StringComparison.OrdinalIgnoreCase))
        {
            return $"{providerName} đang quá tải";
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return $"{providerName} phản hồi quá lâu";
        }

        return $"{providerName} tạm thời chưa sẵn sàng";
    }

    private static TimeSpan ExtractRetryAfterDelay(string message)
    {
        const string marker = "Please retry in ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return TimeSpan.FromSeconds(75);
        }

        var start = markerIndex + marker.Length;
        var end = message.IndexOf('s', start);
        if (end <= start)
        {
            return TimeSpan.FromSeconds(75);
        }

        var secondsText = message[start..end].Trim();
        return double.TryParse(secondsText, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds + 5, 30, 6 * 60 * 60))
            : TimeSpan.FromSeconds(75);
    }

    private async Task EnsureQueuedSummaryJobsAsync(CancellationToken cancellationToken)
    {
        var booksNeedingSummary = await _dbContext.Books
            .AsNoTracking()
            .Where(book =>
                book.SourceType == BookSourceType.UserUpload &&
                book.ProcessingStatus == BookProcessingStatus.Extracted &&
                !book.IsSummaryReady &&
                book.ContentChunks.Any() &&
                !book.ProcessingJobs.Any(job =>
                    job.Type == ProcessingJobType.SummarizeBook &&
                    (job.Status == ProcessingJobStatus.Queued || job.Status == ProcessingJobStatus.Running)))
            .Select(book => new { book.Id, book.OwnerUserId })
            .ToListAsync(cancellationToken);

        if (booksNeedingSummary.Count == 0)
        {
            return;
        }

        foreach (var book in booksNeedingSummary)
        {
            _dbContext.ProcessingJobs.Add(new ProcessingJob
            {
                BookId = book.Id,
                UserId = book.OwnerUserId,
                Type = ProcessingJobType.SummarizeBook,
                Status = ProcessingJobStatus.Queued,
                ProgressPercent = 0,
                CurrentStep = "Đã sẵn sàng tạo bản tóm tắt.",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSummaryJobFailedAsync(ProcessingJob job, string message, CancellationToken cancellationToken)
    {
        var cleanMessage = string.IsNullOrWhiteSpace(message)
            ? "Không tạo được tóm tắt từ nội dung đã trích xuất."
            : message;

        job.Status = ProcessingJobStatus.Failed;
        job.ProgressPercent = 100;
        job.CurrentStep = "Tạo tóm tắt thất bại.";
        job.ErrorMessage = cleanMessage;
        job.NextRunAt = null;
        job.FinishedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        job.Book.ProcessingStatus = BookProcessingStatus.Failed;
        job.Book.IsSummaryReady = false;
        job.Book.FailedReason = cleanMessage;
        job.Book.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await NotifyBookFailedAsync(job.Book, $"Tạo tóm tắt: {cleanMessage}", cancellationToken);
    }

    private async Task EnsureQueuedAudioJobsAsync(CancellationToken cancellationToken)
    {
        if (!_audioGenerationService.IsConfigured)
        {
            return;
        }

        var booksNeedingAudio = await _dbContext.Books
            .AsNoTracking()
            .Where(book =>
                book.ProcessingStatus == BookProcessingStatus.SummaryReady &&
                book.IsSummaryReady &&
                !book.IsAudioReady &&
                string.IsNullOrWhiteSpace(book.FailedReason) &&
                book.GeneratedSummaries.Any() &&
                !book.Audios.Any(audio => audio.Status == AudioStatus.Ready) &&
                !book.ProcessingJobs.Any(job =>
                    job.Type == ProcessingJobType.GenerateAudio &&
                    (job.Status == ProcessingJobStatus.Queued || job.Status == ProcessingJobStatus.Running)))
            .Select(book => new { book.Id, book.OwnerUserId })
            .ToListAsync(cancellationToken);

        if (booksNeedingAudio.Count == 0)
        {
            return;
        }

        foreach (var book in booksNeedingAudio)
        {
            _dbContext.ProcessingJobs.Add(new ProcessingJob
            {
                BookId = book.Id,
                UserId = book.OwnerUserId,
                Type = ProcessingJobType.GenerateAudio,
                Status = ProcessingJobStatus.Queued,
                ProgressPercent = 0,
                CurrentStep = "Đã sẵn sàng tạo audio từ bản tóm tắt.",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasActiveSummaryJobAsync(int bookId, CancellationToken cancellationToken)
    {
        return await _dbContext.ProcessingJobs.AnyAsync(
            job =>
                job.BookId == bookId &&
                job.Type == ProcessingJobType.SummarizeBook &&
                (job.Status == ProcessingJobStatus.Queued || job.Status == ProcessingJobStatus.Running),
            cancellationToken);
    }

    private async Task<bool> HasActiveAudioJobAsync(int bookId, CancellationToken cancellationToken)
    {
        return await _dbContext.ProcessingJobs.AnyAsync(
            job =>
                job.BookId == bookId &&
                job.Type == ProcessingJobType.GenerateAudio &&
                (job.Status == ProcessingJobStatus.Queued || job.Status == ProcessingJobStatus.Running),
            cancellationToken);
    }

    private async Task<bool> SkipUnsupportedJobAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        job.Status = ProcessingJobStatus.Failed;
        job.ProgressPercent = 100;
        job.CurrentStep = "Loại job này chưa được worker hỗ trợ.";
        job.ErrorMessage = "Unsupported processing job type.";
        job.NextRunAt = null;
        job.FinishedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task MarkJobRunningAsync(ProcessingJob job, string currentStep, CancellationToken cancellationToken)
    {
        job.Status = ProcessingJobStatus.Running;
        job.ProgressPercent = 5;
        job.CurrentStep = currentStep;
        job.ErrorMessage = null;
        job.NextRunAt = null;
        job.StartedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkAudioJobFailedAsync(ProcessingJob job, string message, CancellationToken cancellationToken)
    {
        var cleanMessage = string.IsNullOrWhiteSpace(message)
            ? "Không tạo được audio."
            : message;

        job.Status = ProcessingJobStatus.Failed;
        job.ProgressPercent = 100;
        job.CurrentStep = "Tạo audio thất bại.";
        job.ErrorMessage = cleanMessage;
        job.NextRunAt = null;
        job.FinishedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        var hasReadyAudio = job.Book.Audios.Any(audio => audio.Status == AudioStatus.Ready);
        job.Book.ProcessingStatus = hasReadyAudio ? BookProcessingStatus.Ready : BookProcessingStatus.SummaryReady;
        job.Book.IsAudioReady = hasReadyAudio;
        job.Book.FailedReason = cleanMessage;
        job.Book.UpdatedAt = DateTime.UtcNow;

        _dbContext.BookAudios.Add(new BookAudio
        {
            BookId = job.BookId,
            AudioUrl = string.Empty,
            DurationSeconds = 0,
            VoiceName = null,
            Provider = null,
            Status = AudioStatus.Failed,
            FailedReason = cleanMessage,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await NotifyBookFailedAsync(job.Book, $"Tạo audio: {cleanMessage}", cancellationToken);
    }

    private async Task UpdateProgressAsync(
        ProcessingJob job,
        BookProcessingStatus bookStatus,
        int progress,
        string currentStep,
        CancellationToken cancellationToken)
    {
        job.Book.ProcessingStatus = bookStatus;
        job.Book.UpdatedAt = DateTime.UtcNow;
        job.ProgressPercent = progress;
        job.CurrentStep = currentStep;
        job.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkJobFailedAsync(ProcessingJob job, string message, CancellationToken cancellationToken)
    {
        var cleanMessage = string.IsNullOrWhiteSpace(message)
            ? "Không xử lý được file upload."
            : message;

        job.Status = ProcessingJobStatus.Failed;
        job.ProgressPercent = 100;
        job.CurrentStep = "Xử lý file thất bại.";
        job.ErrorMessage = cleanMessage;
        job.NextRunAt = null;
        job.FinishedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;

        job.Book.ProcessingStatus = BookProcessingStatus.Failed;
        job.Book.FailedReason = cleanMessage;
        job.Book.UpdatedAt = DateTime.UtcNow;

        foreach (var file in job.Book.Files)
        {
            file.UploadStatus = BookFileUploadStatus.Failed;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency exception occurred while marking job {JobId} as failed. Reloading and retrying.", job.Id);
            
            try
            {
                await _dbContext.Entry(job).ReloadAsync(cancellationToken);
                if (job.Book != null)
                {
                    await _dbContext.Entry(job.Book).ReloadAsync(cancellationToken);
                    foreach (var file in job.Book.Files)
                    {
                        await _dbContext.Entry(file).ReloadAsync(cancellationToken);
                    }
                }

                // Re-apply updates
                job.Status = ProcessingJobStatus.Failed;
                job.ProgressPercent = 100;
                job.CurrentStep = "Xử lý file thất bại.";
                job.ErrorMessage = cleanMessage;
                job.NextRunAt = null;
                job.FinishedAt = DateTime.UtcNow;
                job.UpdatedAt = DateTime.UtcNow;

                if (job.Book != null)
                {
                    job.Book.ProcessingStatus = BookProcessingStatus.Failed;
                    job.Book.FailedReason = cleanMessage;
                    job.Book.UpdatedAt = DateTime.UtcNow;

                    foreach (var file in job.Book.Files)
                    {
                        file.UploadStatus = BookFileUploadStatus.Failed;
                    }
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception reloadEx)
            {
                _logger.LogError(reloadEx, "Failed to reload and retry marking job {JobId} as failed.", job.Id);
            }
        }

        if (job.Book != null)
        {
            await NotifyBookFailedAsync(job.Book, $"Trích xuất nội dung: {cleanMessage}", cancellationToken);
        }
    }

    private async Task<string> SaveExtractedTextAsync(string? userId, int bookId, string text, CancellationToken cancellationToken)
    {
        var ownerFolder = string.IsNullOrWhiteSpace(userId) ? "system" : userId;
        var folder = Path.Combine(_environment.ContentRootPath, "App_Data", "extracted", ownerFolder);
        Directory.CreateDirectory(folder);

        var fileName = $"book-{bookId}-extracted.txt";
        var physicalPath = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(physicalPath, text, Encoding.UTF8, cancellationToken);

        return Path.Combine("App_Data", "extracted", ownerFolder, fileName);
    }

    private static List<BookContentChunk> BuildChunks(int bookId, string text)
    {
        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var chunks = new List<BookContentChunk>();
        if (words.Count == 0)
        {
            return chunks;
        }

        var index = 0;
        for (var start = 0; start < words.Count; start += MaxChunkWords - ChunkOverlapWords)
        {
            var chunkWords = words.Skip(start).Take(MaxChunkWords).ToList();
            if (chunkWords.Count == 0)
            {
                break;
            }

            var content = string.Join(' ', chunkWords);
            chunks.Add(new BookContentChunk
            {
                BookId = bookId,
                ChunkIndex = index,
                Content = content,
                TokenCount = EstimateTokenCount(content),
                CreatedAt = DateTime.UtcNow
            });

            index++;

            if (start + MaxChunkWords >= words.Count)
            {
                break;
            }
        }

        return chunks;
    }

    private static int EstimateTokenCount(string text)
    {
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }

    private static int EstimateReadingMinutes(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(words / 220.0));
    }
}
