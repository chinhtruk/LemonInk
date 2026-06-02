using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Audio;
using ZenRead.Services.Ai;
using ZenRead.Services.Covers;
using ZenRead.Services.Email;
using ZenRead.Services.Summarization;
using ZenRead.ViewModels;

namespace ZenRead.Services.Uploads;

public class BookUploadService : IBookUploadService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".epub",
        ".txt"
    };

    private const long MaxFileSizeBytes = 30 * 1024 * 1024;

    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IBookCoverService _coverService;
    private readonly IEmailNotificationService _emailNotifications;
    private readonly ILogger<BookUploadService> _logger;
    private readonly AiSummarizationOptions _summarizationOptions;
    private readonly AudioGenerationOptions _audioOptions;

    public BookUploadService(
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IBookCoverService coverService,
        IEmailNotificationService emailNotifications,
        IOptions<AiSummarizationOptions> summarizationOptions,
        IOptions<AudioGenerationOptions> audioOptions,
        ILogger<BookUploadService> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _coverService = coverService;
        _emailNotifications = emailNotifications;
        _summarizationOptions = summarizationOptions.Value;
        _audioOptions = audioOptions.Value;
        _logger = logger;
    }

    public async Task<BookUploadFormViewModel> BuildUploadFormAsync(string userId)
    {
        return new BookUploadFormViewModel
        {
            RecentUploads = await GetUserUploadsAsync(userId, take: 8)
        };
    }

    public async Task<List<UserUploadBookItem>> GetUserUploadsAsync(string userId, int? take = null)
    {
        var query = _dbContext.Books
            .AsNoTracking()
            .Where(book => book.OwnerUserId == userId && book.SourceType == BookSourceType.UserUpload)
            .OrderByDescending(book => book.CreatedAt)
            .Select(book => new UserUploadBookItem
            {
                Id = book.Id,
                Title = book.Title,
                AuthorName = book.AuthorName,
                ProcessingStatus = book.ProcessingStatus,
                CreatedAt = book.CreatedAt,
                OriginalFileName = book.Files
                    .OrderByDescending(file => file.CreatedAt)
                    .Select(file => file.OriginalFileName)
                    .FirstOrDefault() ?? "File sách",
                LatestJobStatus = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => (ProcessingJobStatus?)job.Status)
                    .FirstOrDefault(),
                LatestJobProgress = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.ProgressPercent)
                    .FirstOrDefault(),
                LatestJobStep = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.CurrentStep)
                    .FirstOrDefault() ?? "Đang chờ xử lý",
                LatestJobRetryCount = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.RetryCount)
                    .FirstOrDefault(),
                LatestJobNextRunAt = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.NextRunAt)
                    .FirstOrDefault(),
                LatestJobStartedAt = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.StartedAt)
                    .FirstOrDefault(),
                FailedReason = book.FailedReason,
                ExtractedCharacterCount = book.ContentChunks
                    .Sum(chunk => (int?)chunk.Content.Length) ?? 0,
                SummaryContentCharacterCount = book.SummarySections
                    .Sum(section => (int?)section.ContentHtml.Length) ?? 0,
                CompletedSummaryPassCount = _dbContext.BookSummaryPassCheckpoints
                    .Count(checkpoint => checkpoint.BookId == book.Id)
            });

        if (take is > 0)
        {
            query = query.Take(take.Value);
        }

        var uploads = await query.ToListAsync();
        await PopulateEstimateInputsAsync(uploads);
        foreach (var upload in uploads)
        {
            PopulatePipelineProgress(upload);
        }

        return uploads;
    }

    public async Task<UserUploadBookItem?> GetProcessingStatusAsync(int bookId, string userId)
    {
        var status = await _dbContext.Books
            .AsNoTracking()
            .Where(book => book.Id == bookId && book.OwnerUserId == userId && book.SourceType == BookSourceType.UserUpload)
            .Select(book => new UserUploadBookItem
            {
                Id = book.Id,
                Title = book.Title,
                AuthorName = book.AuthorName,
                ProcessingStatus = book.ProcessingStatus,
                CreatedAt = book.CreatedAt,
                OriginalFileName = book.Files
                    .OrderByDescending(file => file.CreatedAt)
                    .Select(file => file.OriginalFileName)
                    .FirstOrDefault() ?? "File sách",
                LatestJobStatus = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => (ProcessingJobStatus?)job.Status)
                    .FirstOrDefault(),
                LatestJobProgress = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.ProgressPercent)
                    .FirstOrDefault(),
                LatestJobStep = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.CurrentStep)
                    .FirstOrDefault() ?? "Đang chờ xử lý",
                LatestJobRetryCount = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.RetryCount)
                    .FirstOrDefault(),
                LatestJobNextRunAt = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.NextRunAt)
                    .FirstOrDefault(),
                LatestJobStartedAt = book.ProcessingJobs
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.StartedAt)
                    .FirstOrDefault(),
                FailedReason = book.FailedReason,
                ExtractedCharacterCount = book.ContentChunks
                    .Sum(chunk => (int?)chunk.Content.Length) ?? 0,
                SummaryContentCharacterCount = book.SummarySections
                    .Sum(section => (int?)section.ContentHtml.Length) ?? 0,
                CompletedSummaryPassCount = _dbContext.BookSummaryPassCheckpoints
                    .Count(checkpoint => checkpoint.BookId == book.Id)
            })
            .FirstOrDefaultAsync();

        if (status is not null)
        {
            await PopulateEstimateInputsAsync([status]);
            PopulatePipelineProgress(status);
        }

        return status;
    }

    public async Task<BookUploadResult> UploadAsync(BookUploadFormViewModel form, string userId)
    {
        if (form.File is null || form.File.Length == 0)
        {
            return Failed("Bạn cần chọn một file sách để tải lên.");
        }

        if (form.File.Length > MaxFileSizeBytes)
        {
            return Failed("File quá lớn. Hiện tại LemonInk nhận file tối đa 30MB.");
        }

        var extension = Path.GetExtension(form.File.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            return Failed("Định dạng chưa được hỗ trợ. Hãy dùng PDF, EPUB hoặc TXT.");
        }

        var validationError = await ValidateFileContentAsync(form.File, extension);
        if (validationError is not null)
        {
            return Failed(validationError);
        }

        var originalFileName = Path.GetFileName(form.File.FileName);
        var title = string.IsNullOrWhiteSpace(form.Title)
            ? Path.GetFileNameWithoutExtension(originalFileName)
            : form.Title.Trim();

        var authorName = string.IsNullOrWhiteSpace(form.AuthorName)
            ? "Chưa xác định"
            : form.AuthorName.Trim();

        var category = string.IsNullOrWhiteSpace(form.Category)
            ? "Sách cá nhân"
            : form.Category.Trim();

        var language = string.IsNullOrWhiteSpace(form.Language)
            ? "vi"
            : form.Language.Trim().ToLowerInvariant();

        var uploadRoot = Path.Combine(_environment.ContentRootPath, "App_Data", "uploads", "books", userId);
        Directory.CreateDirectory(uploadRoot);

        var storedFileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}";
        var physicalPath = Path.Combine(uploadRoot, storedFileName);

        await using (var stream = File.Create(physicalPath))
        {
            await form.File.CopyToAsync(stream);
        }

        var now = DateTime.UtcNow;
        var book = new Book
        {
            Title = title,
            Slug = $"{Slugify(title)}-{Guid.NewGuid():N}".ToLowerInvariant(),
            AuthorName = authorName,
            Description = "Sách cá nhân do người dùng tải lên.",
            Introduction = "LemonInk đã nhận sách của bạn. Bản tóm tắt, audio và chat theo nội dung sách sẽ sẵn sàng sau khi pipeline AI được xử lý.",
            Category = category,
            CoverGradient = string.Empty,
            CoverSvg = string.Empty,
            SourceType = BookSourceType.UserUpload,
            Visibility = BookVisibility.Private,
            ProcessingStatus = BookProcessingStatus.Uploaded,
            OwnerUserId = userId,
            Language = language,
            ReadingTimeMinutes = 0,
            IsSummaryReady = false,
            IsAudioReady = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        book.CoverGradient = _coverService.BuildGradient(book);
        book.CoverUrl = await _coverService.EnsureCoverAsync(book, CancellationToken.None);

        var bookFile = new BookFile
        {
            Book = book,
            OwnerUserId = userId,
            OriginalFileName = originalFileName,
            StoredFilePath = Path.Combine("App_Data", "uploads", "books", userId, storedFileName),
            FileType = ResolveFileType(extension),
            FileSizeBytes = form.File.Length,
            UploadStatus = BookFileUploadStatus.Uploaded,
            CreatedAt = now
        };

        var job = new ProcessingJob
        {
            Book = book,
            UserId = userId,
            Type = ProcessingJobType.ExtractText,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            CurrentStep = "Đã nhận file, chờ trích xuất nội dung.",
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Books.Add(book);
        _dbContext.BookFiles.Add(bookFile);
        _dbContext.ProcessingJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        try
        {
            var owner = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == userId);
            await _emailNotifications.SendBookUploadReceivedAsync(owner, book);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not send upload acknowledgement email for book {BookId}.", book.Id);
        }

        return new BookUploadResult
        {
            Succeeded = true,
            BookId = book.Id,
            Message = "Đã tải sách lên. LemonInk đã tạo job xử lý để chuẩn bị cho bước tóm tắt bằng AI."
        };
    }

    private static async Task<string?> ValidateFileContentAsync(IFormFile file, string extension)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            var valid = extension switch
            {
                ".pdf" => await HasPdfHeaderAsync(stream),
                ".epub" => IsEpubArchive(stream),
                ".txt" => await HasReadableUtf8TextAsync(stream),
                _ => false
            };

            return valid
                ? null
                : extension switch
                {
                    ".pdf" => "File được chọn không phải PDF hợp lệ.",
                    ".epub" => "File được chọn không phải EPUB hợp lệ.",
                    ".txt" => "File TXT phải là văn bản UTF-8 có thể đọc được.",
                    _ => "Nội dung file không hợp lệ."
                };
        }
        catch (InvalidDataException)
        {
            return "File đã hỏng hoặc không đúng định dạng đã chọn.";
        }
        catch (IOException)
        {
            return "Không thể đọc file upload. Hãy thử chọn lại file.";
        }
    }

    private static async Task<bool> HasPdfHeaderAsync(Stream stream)
    {
        var header = new byte[5];
        var bytesRead = await stream.ReadAsync(header.AsMemory());
        return bytesRead == header.Length
            && Encoding.ASCII.GetString(header) == "%PDF-";
    }

    private static bool IsEpubArchive(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var mimetypeEntry = archive.GetEntry("mimetype");
        if (mimetypeEntry is null)
        {
            return false;
        }

        using var reader = new StreamReader(mimetypeEntry.Open(), Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        return string.Equals(reader.ReadToEnd().Trim(), "application/epub+zip", StringComparison.Ordinal);
    }

    private static async Task<bool> HasReadableUtf8TextAsync(Stream stream)
    {
        try
        {
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);
            var sample = new char[2048];
            var charactersRead = await reader.ReadAsync(sample.AsMemory());
            if (charactersRead == 0)
            {
                return false;
            }

            var text = new string(sample, 0, charactersRead);
            return !text.Contains('\0')
                && text.Any(character => !char.IsWhiteSpace(character) && character != '\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private void PopulatePipelineProgress(UserUploadBookItem item)
    {
        var stepProgress = Math.Clamp(item.LatestJobProgress, 0, 100);
        var displayedSummaryProgress = ResolveDisplayedSummaryProgress(item, stepProgress);

        item.OverallProgressPercent = item.ProcessingStatus switch
        {
            BookProcessingStatus.Uploaded => Math.Max(4, ScaleProgress(stepProgress, 4, 26)),
            BookProcessingStatus.ExtractingText => ScaleProgress(stepProgress, 4, 30),
            BookProcessingStatus.Extracted => 30,
            BookProcessingStatus.Summarizing => ScaleProgress(displayedSummaryProgress, 30, 72),
            BookProcessingStatus.SummaryReady => 72,
            BookProcessingStatus.GeneratingAudio => ScaleProgress(stepProgress, 72, 99),
            BookProcessingStatus.Ready => 100,
            BookProcessingStatus.Failed => Math.Max(4, stepProgress),
            _ => Math.Max(4, stepProgress)
        };

        item.EstimatedTimeText = ResolveProviderWaitText(item) ?? item.ProcessingStatus switch
        {
            BookProcessingStatus.Uploaded => "Dự kiến còn 2-4 phút",
            BookProcessingStatus.ExtractingText => "Dự kiến còn 2-3 phút",
            BookProcessingStatus.Extracted => ResolveSummaryEstimate(item),
            BookProcessingStatus.Summarizing when stepProgress >= 70 => "Đang hoàn thiện bản tóm tắt",
            BookProcessingStatus.Summarizing => ResolveSummaryEstimate(item),
            BookProcessingStatus.SummaryReady => ResolveAudioEstimate(item),
            BookProcessingStatus.GeneratingAudio when stepProgress >= 85 => "Đang hoàn thiện file audio",
            BookProcessingStatus.GeneratingAudio => ResolveAudioEstimate(item),
            BookProcessingStatus.Ready => "Đã hoàn tất",
            BookProcessingStatus.Failed => "Đã dừng xử lý",
            _ => "Đang ước tính..."
        };
    }

    private static string? ResolveProviderWaitText(UserUploadBookItem item)
    {
        if (item.LatestJobStatus != ProcessingJobStatus.Queued ||
            item.LatestJobNextRunAt is not { } nextRunAt)
        {
            return null;
        }

        var remainingSeconds = Math.Max(1, (int)Math.Ceiling((nextRunAt - DateTime.UtcNow).TotalSeconds));
        if (item.LatestJobStep.Contains("TTS", StringComparison.OrdinalIgnoreCase))
        {
            return item.LatestJobStep.Contains("giới hạn lượt gọi", StringComparison.OrdinalIgnoreCase)
                ? $"TTS hết lượt gọi hiện tại, thử lại sau {FormatWait(remainingSeconds)}"
                : $"TTS đang chờ retry, thử lại sau {FormatWait(remainingSeconds)}";
        }

        if (item.LatestJobStep.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (item.LatestJobStep.Contains("giới hạn lượt gọi", StringComparison.OrdinalIgnoreCase))
            {
                return $"Gemini hết lượt gọi hiện tại, thử lại sau {FormatWait(remainingSeconds)}";
            }

            return item.LatestJobStep.Contains("quá tải", StringComparison.OrdinalIgnoreCase)
                ? $"Gemini đang quá tải, thử lại sau {FormatWait(remainingSeconds)}"
                : $"Gemini đang chờ retry, thử lại sau {FormatWait(remainingSeconds)}";
        }

        if (item.LatestJobStep.Contains("OCR", StringComparison.OrdinalIgnoreCase))
        {
            return $"OCR đang bận, thử lại sau {FormatWait(remainingSeconds)}";
        }

        return null;
    }

    private async Task PopulateEstimateInputsAsync(IReadOnlyCollection<UserUploadBookItem> items)
    {
        if (!items.Any(item => item.ProcessingStatus is BookProcessingStatus.Extracted or BookProcessingStatus.Summarizing))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var durations = await _dbContext.AiModelOperationEvents
            .AsNoTracking()
            .Where(operation =>
                operation.Monitor.Task == nameof(AiModelTask.Summarization) &&
                operation.OccurredAt >= cutoff &&
                operation.DurationMilliseconds >= 1000)
            .OrderByDescending(operation => operation.OccurredAt)
            .Take(20)
            .Select(operation => operation.DurationMilliseconds)
            .ToListAsync();
        if (durations.Count == 0)
        {
            return;
        }

        durations.Sort();
        var percentileIndex = Math.Clamp((int)Math.Ceiling(durations.Count * 0.75) - 1, 0, durations.Count - 1);
        var requestSeconds = Math.Clamp((int)Math.Ceiling(durations[percentileIndex] / 1000d), 30, 180);

        foreach (var item in items)
        {
            item.SummaryRequestDurationSeconds = requestSeconds;
        }
    }

    private int ResolveDisplayedSummaryProgress(UserUploadBookItem item, int jobProgress)
    {
        if (jobProgress >= 70)
        {
            return jobProgress;
        }

        var requests = ResolveSummaryRequestCount(item.ExtractedCharacterCount);
        if (requests <= 1 || item.CompletedSummaryPassCount == 0)
        {
            return jobProgress;
        }

        var completed = Math.Clamp(item.CompletedSummaryPassCount, 0, requests - 1);
        var checkpointProgress = 25 + (int)Math.Round(42d * completed / requests);
        return Math.Max(jobProgress, checkpointProgress);
    }

    private string ResolveSummaryEstimate(UserUploadBookItem item)
    {
        if (item.ExtractedCharacterCount <= 0)
        {
            return "Đang ước tính bước tóm tắt";
        }

        var requestCount = ResolveSummaryRequestCount(item.ExtractedCharacterCount);
        var completed = Math.Clamp(item.CompletedSummaryPassCount, 0, Math.Max(0, requestCount - 1));
        var remaining = Math.Max(1, requestCount - completed);
        var delaySeconds = Math.Clamp(_summarizationOptions.MultiPassDelayMilliseconds, 0, 120000) / 1000;
        var observedRequestSeconds = item.SummaryRequestDurationSeconds > 0
            ? item.SummaryRequestDurationSeconds
            : 60;
        var requestLowerSeconds = Math.Max(40, (int)Math.Floor(observedRequestSeconds * 0.75));
        var requestUpperSeconds = Math.Max(
            item.LatestJobRetryCount > 0 ? 180 : 120,
            (int)Math.Ceiling(observedRequestSeconds * 1.5));
        var lowerSeconds = (remaining * requestLowerSeconds) + (Math.Max(0, remaining - 1) * delaySeconds);
        var upperSeconds = (remaining * requestUpperSeconds) + (Math.Max(0, remaining - 1) * delaySeconds);

        return $"Ước tính tóm tắt: {FormatMinuteRange(lowerSeconds, upperSeconds)}";
    }

    private string ResolveAudioEstimate(UserUploadBookItem item)
    {
        if (item.SummaryContentCharacterCount <= 0)
        {
            return "Đang chuẩn bị audio";
        }

        var segmentCount = ResolveAudioSegmentCount(item.SummaryContentCharacterCount);
        var completedSegments = ParseCompletedAudioSegments(item.LatestJobStep);
        var remainingSegments = Math.Max(1, segmentCount - completedSegments);
        var requestIntervalSeconds = Math.Clamp(_audioOptions.Gemini.MinimumRequestIntervalMilliseconds, 0, 120000) / 1000;
        var observedSegmentSeconds = ResolveObservedAudioSegmentSeconds(item, completedSegments);
        var lowerSegmentSeconds = observedSegmentSeconds > 0
            ? Math.Max(requestIntervalSeconds, (int)Math.Floor(observedSegmentSeconds * 0.8))
            : Math.Max(90, requestIntervalSeconds);
        var upperSegmentSeconds = observedSegmentSeconds > 0
            ? Math.Max(150, (int)Math.Ceiling(observedSegmentSeconds * 1.4))
            : Math.Max(180, requestIntervalSeconds + 90);
        var lowerSeconds = remainingSegments * lowerSegmentSeconds;
        var upperSeconds = remainingSegments * upperSegmentSeconds;

        return $"Ước tính audio: {FormatMinuteRange(lowerSeconds, upperSeconds)}";
    }

    private int ResolveSummaryRequestCount(int extractedCharacterCount)
    {
        var maxCharacters = Math.Clamp(_summarizationOptions.MaxCharactersPerSummaryPass, 40000, 200000);
        if (extractedCharacterCount <= maxCharacters)
        {
            return 1;
        }

        return (int)Math.Ceiling((double)extractedCharacterCount / maxCharacters) + 1;
    }

    private int ResolveAudioSegmentCount(int summaryCharacterCount)
    {
        var maxCharacters = Math.Clamp(_audioOptions.MaxInputCharacters, 1800, 5200);
        return Math.Max(1, (int)Math.Ceiling((double)summaryCharacterCount / maxCharacters));
    }

    private static int ParseCompletedAudioSegments(string step)
    {
        if (string.IsNullOrWhiteSpace(step))
        {
            return 0;
        }

        const string marker = "hoàn tất đoạn ";
        var markerIndex = step.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return 0;
        }

        var valueStart = markerIndex + marker.Length;
        var separatorIndex = step.IndexOf('/', valueStart);
        if (separatorIndex <= valueStart)
        {
            return 0;
        }

        return int.TryParse(step[valueStart..separatorIndex], out var completed)
            ? Math.Max(0, completed)
            : 0;
    }

    private static int ResolveObservedAudioSegmentSeconds(UserUploadBookItem item, int completedSegments)
    {
        if (completedSegments <= 0 || item.LatestJobStartedAt is not { } startedAt)
        {
            return 0;
        }

        var elapsedSeconds = Math.Max(1, (DateTime.UtcNow - startedAt).TotalSeconds);
        return (int)Math.Ceiling(elapsedSeconds / completedSegments);
    }

    private static string FormatMinuteRange(int lowerSeconds, int upperSeconds)
    {
        var lowerMinutes = Math.Max(1, (int)Math.Ceiling(lowerSeconds / 60d));
        var upperMinutes = Math.Max(lowerMinutes, (int)Math.Ceiling(upperSeconds / 60d));
        return lowerMinutes == upperMinutes
            ? $"khoảng {lowerMinutes} phút"
            : $"{lowerMinutes}-{upperMinutes} phút";
    }

    private static string FormatWait(int seconds)
    {
        return seconds < 60
            ? $"{seconds} giây"
            : $"{Math.Ceiling(seconds / 60d)} phút";
    }

    private static int ScaleProgress(int stepProgress, int startPercent, int endPercent)
    {
        return startPercent + ((endPercent - startPercent) * stepProgress / 100);
    }

    public async Task<BookUploadResult> RetryProcessingAsync(int bookId, string userId)
    {
        var book = await _dbContext.Books
            .Include(item => item.Files)
            .Include(item => item.ContentChunks)
            .Include(item => item.ProcessingJobs)
            .FirstOrDefaultAsync(item =>
                item.Id == bookId &&
                item.OwnerUserId == userId &&
                item.SourceType == BookSourceType.UserUpload);

        if (book is null)
        {
            return Failed("Không tìm thấy sách upload của bạn.");
        }

        var hasActiveJob = book.ProcessingJobs.Any(job =>
            job.Status == ProcessingJobStatus.Queued ||
            job.Status == ProcessingJobStatus.Running);

        if (hasActiveJob)
        {
            return Failed("Sách này đang có job xử lý. Vui lòng chờ job hiện tại kết thúc.");
        }

        var latestJob = book.ProcessingJobs
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .FirstOrDefault();

        if (book.ProcessingStatus != BookProcessingStatus.Failed &&
            latestJob?.Status != ProcessingJobStatus.Failed)
        {
            return Failed("Chỉ có thể retry khi job mới nhất của sách đang lỗi.");
        }

        var latestFailedJobType = latestJob?.Status == ProcessingJobStatus.Failed
            ? latestJob.Type
            : (ProcessingJobType?)null;

        var now = DateTime.UtcNow;
        var nextJobType = latestFailedJobType switch
        {
            ProcessingJobType.GenerateAudio when book.IsSummaryReady => ProcessingJobType.GenerateAudio,
            ProcessingJobType.SummarizeBook when book.ContentChunks.Count > 0 => ProcessingJobType.SummarizeBook,
            _ => book.ContentChunks.Count > 0 ? ProcessingJobType.SummarizeBook : ProcessingJobType.ExtractText
        };

        book.ProcessingStatus = nextJobType switch
        {
            ProcessingJobType.ExtractText => BookProcessingStatus.Uploaded,
            ProcessingJobType.SummarizeBook => BookProcessingStatus.Extracted,
            ProcessingJobType.GenerateAudio => BookProcessingStatus.SummaryReady,
            _ => BookProcessingStatus.Uploaded
        };
        book.FailedReason = null;
        book.UpdatedAt = now;

        if (nextJobType == ProcessingJobType.ExtractText)
        {
            foreach (var file in book.Files)
            {
                file.UploadStatus = BookFileUploadStatus.Uploaded;
            }
        }

        var retryCount = book.ProcessingJobs.Count == 0
            ? 1
            : book.ProcessingJobs.Max(job => job.RetryCount) + 1;

        _dbContext.ProcessingJobs.Add(new ProcessingJob
        {
            BookId = book.Id,
            UserId = userId,
            Type = nextJobType,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            RetryCount = retryCount,
            CurrentStep = nextJobType switch
            {
                ProcessingJobType.ExtractText => "Đã đưa sách vào hàng đợi trích xuất lại.",
                ProcessingJobType.SummarizeBook => "Đã đưa sách vào hàng đợi tóm tắt lại.",
                ProcessingJobType.GenerateAudio => "Đã đưa sách vào hàng đợi tạo audio lại.",
                _ => "Đã đưa sách vào hàng đợi xử lý lại."
            },
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync();

        return new BookUploadResult
        {
            Succeeded = true,
            BookId = book.Id,
            Message = "Đã tạo job retry cho sách này."
        };
    }

    public async Task<BookUploadResult> ReprocessFromSourceAsync(int bookId, string userId)
    {
        var book = await _dbContext.Books
            .Include(item => item.Files)
            .Include(item => item.ProcessingJobs)
            .FirstOrDefaultAsync(item =>
                item.Id == bookId &&
                item.OwnerUserId == userId &&
                item.SourceType == BookSourceType.UserUpload);

        if (book is null)
        {
            return Failed("Không tìm thấy sách upload của bạn.");
        }

        if (book.ProcessingJobs.Any(job =>
                job.Status == ProcessingJobStatus.Queued ||
                job.Status == ProcessingJobStatus.Running))
        {
            return Failed("Sách này đang được xử lý. Hãy chờ hoàn tất trước khi tạo lại.");
        }

        var sourceFile = book.Files
            .OrderByDescending(file => file.CreatedAt)
            .FirstOrDefault();

        if (sourceFile is null)
        {
            return Failed("Không tìm thấy file gốc để xử lý lại sách này.");
        }

        var now = DateTime.UtcNow;
        var retryCount = book.ProcessingJobs.Count == 0
            ? 1
            : book.ProcessingJobs.Max(job => job.RetryCount) + 1;

        sourceFile.UploadStatus = BookFileUploadStatus.Uploaded;
        book.ProcessingStatus = BookProcessingStatus.Uploaded;
        book.IsSummaryReady = false;
        book.IsAudioReady = false;
        book.FailedReason = null;
        book.UpdatedAt = now;

        _dbContext.ProcessingJobs.Add(new ProcessingJob
        {
            BookId = book.Id,
            UserId = userId,
            Type = ProcessingJobType.ExtractText,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            RetryCount = retryCount,
            CurrentStep = "Đã đưa file gốc vào hàng đợi trích xuất và tóm tắt lại.",
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync();

        return new BookUploadResult
        {
            Succeeded = true,
            BookId = book.Id,
            Message = "Đã bắt đầu tạo lại bản tóm tắt và audio từ file gốc."
        };
    }

    public async Task<BookUploadResult> DeleteUploadAsync(int bookId, string userId)
    {
        var book = await _dbContext.Books
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item =>
                item.Id == bookId &&
                item.OwnerUserId == userId &&
                item.SourceType == BookSourceType.UserUpload);

        if (book is null)
        {
            return Failed("Không tìm thấy sách upload của bạn.");
        }

        var relativePaths = book.Files
            .SelectMany(file => new[] { file.StoredFilePath, file.ExtractedTextPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();

        _dbContext.Books.Remove(book);
        await _dbContext.SaveChangesAsync();

        foreach (var relativePath in relativePaths)
        {
            DeleteRelativeFile(relativePath);
        }

        return new BookUploadResult
        {
            Succeeded = true,
            BookId = book.Id,
            Message = "Đã xóa sách upload khỏi thư viện cá nhân."
        };
    }

    private static BookFileType ResolveFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => BookFileType.Pdf,
            ".epub" => BookFileType.Epub,
            ".txt" => BookFileType.Txt,
            _ => BookFileType.Other
        };
    }

    private static BookUploadResult Failed(string message)
    {
        return new BookUploadResult
        {
            Succeeded = false,
            Message = message
        };
    }

    private static string Slugify(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasDash = false;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            if (char.IsLetterOrDigit(lower))
            {
                builder.Append(lower);
                previousWasDash = false;
                continue;
            }

            if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "sach-upload" : slug;
    }

    private void DeleteRelativeFile(string relativePath)
    {
        var contentRoot = Path.GetFullPath(_environment.ContentRootPath);
        var physicalPath = Path.GetFullPath(Path.Combine(contentRoot, relativePath));

        if (!physicalPath.StartsWith(contentRoot, StringComparison.Ordinal) || !File.Exists(physicalPath))
        {
            return;
        }

        File.Delete(physicalPath);
    }
}
