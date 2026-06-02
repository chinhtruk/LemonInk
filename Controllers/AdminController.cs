using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Ai;
using ZenRead.Services.Audio;
using ZenRead.Services.Covers;
using ZenRead.Services.Summarization;
using ZenRead.ViewModels;

namespace ZenRead.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IBookCoverService _coverService;
    private readonly IAiModelRouter _aiModelRouter;
    private readonly AiSummarizationOptions _aiOptions;
    private readonly AudioGenerationOptions _audioOptions;
    private readonly IConfiguration _configuration;

    public AdminController(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IBookCoverService coverService,
        IAiModelRouter aiModelRouter,
        IOptions<AiSummarizationOptions> aiOptions,
        IOptions<AudioGenerationOptions> audioOptions,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _environment = environment;
        _coverService = coverService;
        _aiModelRouter = aiModelRouter;
        _aiOptions = aiOptions.Value;
        _audioOptions = audioOptions.Value;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var stats = await BuildDashboardStatsAsync(cancellationToken);
        var operations = await BuildDashboardOperationsAsync(cancellationToken);
        var books = await GetBookRowsAsync(8, cancellationToken);
        var jobs = await GetJobRowsAsync(8, cancellationToken);

        var failedJobs = jobs.Where(job => job.Status == ToStatusCss(ProcessingJobStatus.Failed)).ToList();
        var runningJobs = jobs.Where(job => job.Status == ToStatusCss(ProcessingJobStatus.Running)).ToList();

        var viewModel = new AdminDashboardViewModel
        {
            Stats = stats,
            Operations = operations,
            Books = books,
            Jobs = jobs,
            Notifications = BuildNotifications(failedJobs, runningJobs, (int)Math.Round(stats.GrowthPercent * stats.TotalBooks / 100m), stats.TotalBooks),
            RecentActivities = jobs
                .Take(5)
                .Select(job => new RecentActivity
                {
                    Action = job.Status,
                    Detail = $"{job.Type} cho \"{job.BookTitle}\": {job.CurrentStep}",
                    Timestamp = job.UpdatedAt
                })
                .ToList()
        };

        return View(viewModel);
    }

    [HttpGet]
    [Route("Admin/Books")]
    public async Task<IActionResult> Books(CancellationToken cancellationToken)
    {
        var viewModel = new AdminBooksPageViewModel
        {
            Books = await GetBookRowsAsync(null, cancellationToken)
        };

        return View(viewModel);
    }

    [HttpGet]
    [Route("Admin/Books/CreateCurated")]
    public IActionResult CreateCurated()
    {
        return View(new AdminCreateCuratedBookViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/CreateCurated")]
    public async Task<IActionResult> CreateCurated(AdminCreateCuratedBookViewModel form, CancellationToken cancellationToken)
    {
        ValidateCuratedBookForm(form);
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        var chapters = ParseChapters(form.ChaptersText);
        var takeaways = ParseTakeaways(form.TakeawaysText);
        var now = DateTime.UtcNow;
        var summaryText = NormalizeText(form.Introduction);

        var book = new Book
        {
            Title = form.Title.Trim(),
            Slug = await BuildUniqueSlugAsync(form.Title, cancellationToken),
            AuthorName = form.AuthorName.Trim(),
            Description = form.Description.Trim(),
            Introduction = summaryText,
            Category = form.Category.Trim(),
            CoverGradient = string.IsNullOrWhiteSpace(form.CoverGradient)
                ? string.Empty
                : form.CoverGradient.Trim(),
            CoverSvg = string.Empty,
            SourceType = BookSourceType.Curated,
            Visibility = form.PublishImmediately ? BookVisibility.Public : BookVisibility.Unlisted,
            ProcessingStatus = BookProcessingStatus.SummaryReady,
            Language = "vi",
            Rating = Math.Clamp(form.Rating, 0, 10),
            ReadingTimeMinutes = Math.Max(1, form.ReadingTimeMinutes),
            IsSummaryReady = true,
            IsAudioReady = false,
            PublishedAt = form.PublishImmediately ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        book.CoverGradient = string.IsNullOrWhiteSpace(book.CoverGradient)
            ? _coverService.BuildGradient(book)
            : book.CoverGradient;
        book.CoverUrl = string.IsNullOrWhiteSpace(form.CoverUrl)
            ? await _coverService.EnsureCoverAsync(book, cancellationToken)
            : form.CoverUrl.Trim();

        book.GeneratedSummaries.Add(new GeneratedBookSummary
        {
            ShortSummary = form.Description.Trim(),
            LongSummary = summaryText,
            GeneratedBy = "LemonInk admin",
            EditedByAdmin = true,
            GeneratedAt = now,
            UpdatedAt = now
        });

        for (var index = 0; index < takeaways.Count; index++)
        {
            book.Takeaways.Add(new BookTakeaway
            {
                Content = takeaways[index],
                SortOrder = index + 1
            });
        }

        for (var index = 0; index < chapters.Count; index++)
        {
            var chapter = chapters[index];
            var chapterNumber = index + 1;

            book.SummarySections.Add(new BookSummarySection
            {
                SectionType = SummarySectionType.Chapter,
                ChapterNumber = chapterNumber,
                Title = chapter.Title,
                ContentHtml = chapter.ContentHtml,
                ReadingTimeMinutes = chapter.ReadingTimeMinutes,
                SortOrder = chapterNumber,
                CreatedAt = now,
                UpdatedAt = now
            });

            book.ContentChunks.Add(new BookContentChunk
            {
                ChunkIndex = index,
                Content = chapter.PlainText,
                TokenCount = EstimateTokenCount(chapter.PlainText),
                CreatedAt = now
            });
        }

        _dbContext.Books.Add(book);
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = form.PublishImmediately
            ? $"Đã tạo và publish \"{book.Title}\"."
            : $"Đã tạo \"{book.Title}\" ở trạng thái unlisted.";

        return RedirectToAction(nameof(Books));
    }

    [HttpGet]
    [Route("Admin/Books/EditCurated/{id:int}")]
    public async Task<IActionResult> EditCurated(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .Include(item => item.Takeaways)
            .Include(item => item.SummarySections)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        if (book.SourceType != BookSourceType.Curated)
        {
            TempData["AdminError"] = "Chỉ sửa trực tiếp sách curated. Sách upload nên xử lý qua pipeline.";
            return RedirectToAction(nameof(Books));
        }

        return View("CreateCurated", ToCuratedForm(book));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/EditCurated/{id:int}")]
    public async Task<IActionResult> EditCurated(int id, AdminCreateCuratedBookViewModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        ValidateCuratedBookForm(form);
        if (!ModelState.IsValid)
        {
            return View("CreateCurated", form);
        }

        var book = await _dbContext.Books
            .Include(item => item.GeneratedSummaries)
            .Include(item => item.Takeaways)
            .Include(item => item.SummarySections)
            .Include(item => item.ContentChunks)
            .Include(item => item.Audios)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        if (book.SourceType != BookSourceType.Curated)
        {
            TempData["AdminError"] = "Chỉ sửa trực tiếp sách curated.";
            return RedirectToAction(nameof(Books));
        }

        var audioPaths = book.Audios
            .Select(audio => audio.AudioUrl)
            .Where(path => !string.IsNullOrWhiteSpace(path) && path.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
            .ToList();

        await ApplyCuratedFormToExistingBookAsync(book, form, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var audioPath in audioPaths)
        {
            DeleteRelativeFile(audioPath);
        }

        TempData["AdminSuccess"] = $"Đã cập nhật \"{book.Title}\".";
        return RedirectToAction(nameof(Books));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/Delete/{id:int}")]
    public async Task<IActionResult> DeleteBook(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .Include(item => item.Files)
            .Include(item => item.Audios)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        var title = book.Title;
        var relativePaths = book.Files
            .SelectMany(file => new[] { file.StoredFilePath, file.ExtractedTextPath })
            .Concat(book.Audios.Select(audio => audio.AudioUrl))
            .Where(path => !string.IsNullOrWhiteSpace(path) && path.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .Distinct()
            .ToList();

        _dbContext.Books.Remove(book);
        await _dbContext.SaveChangesAsync(cancellationToken);

        foreach (var relativePath in relativePaths)
        {
            DeleteRelativeFile(relativePath);
        }

        TempData["AdminSuccess"] = $"Đã xóa \"{title}\" khỏi hệ thống.";
        return RedirectToAction(nameof(Books));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/RegenerateSummary/{id:int}")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> RegenerateSummary(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .Include(item => item.ContentChunks)
            .Include(item => item.ProcessingJobs)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        if (book.ContentChunks.Count == 0)
        {
            TempData["AdminError"] = $"\"{book.Title}\" chưa có content chunk để tạo lại summary.";
            return RedirectToAction(nameof(Books));
        }

        if (HasActiveJob(book))
        {
            TempData["AdminError"] = $"\"{book.Title}\" đang có job chạy hoặc chờ xử lý.";
            return RedirectToAction(nameof(Books));
        }

        var now = DateTime.UtcNow;
        book.ProcessingStatus = BookProcessingStatus.Extracted;
        book.IsSummaryReady = false;
        book.IsAudioReady = false;
        book.FailedReason = null;
        book.UpdatedAt = now;

        _dbContext.ProcessingJobs.Add(new ProcessingJob
        {
            BookId = book.Id,
            UserId = book.OwnerUserId,
            Type = ProcessingJobType.SummarizeBook,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            CurrentStep = "Admin đã đưa sách vào hàng đợi tạo lại summary.",
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = $"Đã queue regenerate summary cho \"{book.Title}\".";
        return RedirectToAction(nameof(ProcessingJobs));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/RegenerateAudio/{id:int}")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> RegenerateAudio(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books
            .Include(item => item.GeneratedSummaries)
            .Include(item => item.ProcessingJobs)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (book is null)
        {
            return NotFound();
        }

        if (!book.IsSummaryReady || book.GeneratedSummaries.Count == 0)
        {
            TempData["AdminError"] = $"\"{book.Title}\" chưa có summary để tạo audio.";
            return RedirectToAction(nameof(Books));
        }

        if (HasActiveJob(book))
        {
            TempData["AdminError"] = $"\"{book.Title}\" đang có job chạy hoặc chờ xử lý.";
            return RedirectToAction(nameof(Books));
        }

        var now = DateTime.UtcNow;
        book.ProcessingStatus = BookProcessingStatus.SummaryReady;
        book.FailedReason = null;
        book.UpdatedAt = now;

        _dbContext.ProcessingJobs.Add(new ProcessingJob
        {
            BookId = book.Id,
            UserId = book.OwnerUserId,
            Type = ProcessingJobType.GenerateAudio,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            CurrentStep = "Admin đã đưa sách vào hàng đợi tạo lại audio.",
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = $"Đã queue regenerate audio cho \"{book.Title}\".";
        return RedirectToAction(nameof(ProcessingJobs));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/Publish/{id:int}")]
    public async Task<IActionResult> Publish(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return NotFound();
        }

        if (!book.IsSummaryReady)
        {
            TempData["AdminError"] = "Chỉ publish sách đã có bản tóm tắt.";
            return RedirectToAction(nameof(Books));
        }

        book.Visibility = BookVisibility.Public;
        book.PublishedAt ??= DateTime.UtcNow;
        book.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = $"Đã publish \"{book.Title}\".";
        return RedirectToAction(nameof(Books));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Books/Unpublish/{id:int}")]
    public async Task<IActionResult> Unpublish(int id, CancellationToken cancellationToken)
    {
        var book = await _dbContext.Books.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (book is null)
        {
            return NotFound();
        }

        book.Visibility = book.OwnerUserId is null ? BookVisibility.Unlisted : BookVisibility.Private;
        book.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = $"Đã gỡ publish \"{book.Title}\".";
        return RedirectToAction(nameof(Books));
    }

    [HttpGet]
    [Route("Admin/ProcessingJobs")]
    public async Task<IActionResult> ProcessingJobs(CancellationToken cancellationToken)
    {
        var jobs = await GetJobRowsAsync(80, cancellationToken);

        var viewModel = new AdminProcessingJobsPageViewModel
        {
            QueuedCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Queued, cancellationToken),
            RunningCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Running, cancellationToken),
            FailedCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Failed, cancellationToken),
            SucceededCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Succeeded, cancellationToken),
            Jobs = jobs
        };

        return View(viewModel);
    }

    [HttpGet]
    [Route("Admin/AiHealth")]
    public async Task<IActionResult> AiHealth(CancellationToken cancellationToken)
    {
        var viewModel = await BuildAiHealthViewModelAsync(cancellationToken);
        return View(viewModel);
    }

    [HttpGet]
    [Route("Admin/AiHealthStatus")]
    public async Task<IActionResult> AiHealthStatus(CancellationToken cancellationToken)
    {
        var viewModel = await BuildAiHealthViewModelAsync(cancellationToken);
        return Json(new
        {
            viewModel.HealthyCount,
            viewModel.CooldownCount,
            viewModel.WarningCount,
            viewModel.RecentAiFailures,
            viewModel.ModelCalls24Hours,
            viewModel.SuccessRate24Hours,
            viewModel.QuotaOrRateLimitErrors24Hours,
            models = viewModel.Models.Select(model => new
            {
                model.Task,
                model.Model,
                model.Status,
                model.SuccessCount,
                model.FailureCount,
                model.SuccessRatePercent,
                model.AverageDurationMilliseconds,
                model.QuotaOrRateLimitFailureCount,
                model.LastFailureKind,
                lastSuccessAtText = FormatNullableDate(model.LastSuccessAt),
                lastFailureAtText = FormatNullableDate(model.LastFailureAt),
                cooldownUntilText = FormatNullableDate(model.CooldownUntil),
                model.LastError
            }),
            recentOperations = viewModel.RecentOperations.Select(operation => new
            {
                operation.Task,
                operation.Model,
                operation.Succeeded,
                operation.DurationMilliseconds,
                operation.FailureKind,
                operation.ErrorMessage,
                occurredAtText = operation.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            }),
            pipelineJobs24Hours = viewModel.PipelineJobs24Hours.Select(job => new
            {
                job.Type,
                job.TotalCount,
                job.SucceededCount,
                job.FailedCount,
                job.RetryCount,
                job.SuccessRatePercent,
                job.AverageDurationSeconds,
                job.LatestFailure
            }),
            recentJobs = viewModel.RecentJobs.Select(job => new
            {
                job.Id,
                job.BookId,
                job.BookTitle,
                job.Type,
                job.Status,
                job.ProgressPercent,
                job.CurrentStep,
                job.ErrorMessage,
                job.RetryCount,
                job.CanRetry,
                job.DurationSeconds,
                job.QualityStatus,
                job.QualitySummary,
                job.QualityWarnings,
                updatedAtText = job.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            })
        });
    }

    [HttpGet]
    [Route("Admin/Realtime")]
    public async Task<IActionResult> Realtime(CancellationToken cancellationToken)
    {
        var stats = await BuildDashboardStatsAsync(cancellationToken);
        var operations = await BuildDashboardOperationsAsync(cancellationToken);
        var audioReadyCount = await _dbContext.Books.CountAsync(book => book.IsAudioReady, cancellationToken);
        var jobs = await GetJobRowsAsync(80, cancellationToken);
        var allBooks = await GetBookRowsAsync(null, cancellationToken);
        var users = await GetUserRowsAsync(cancellationToken);
        await ApplyUserDeletePermissionsAsync(users);
        var failedJobs = jobs.Where(job => job.Status == ToStatusCss(ProcessingJobStatus.Failed)).ToList();
        var runningJobs = jobs.Where(job => job.Status == ToStatusCss(ProcessingJobStatus.Running)).ToList();
        var notifications = BuildNotifications(failedJobs, runningJobs, audioReadyCount, stats.TotalBooks);
        var activities = jobs
            .Take(5)
            .Select(job => new RecentActivity
            {
                Action = job.Status,
                Detail = $"{job.Type} cho \"{job.BookTitle}\": {job.CurrentStep}",
                Timestamp = job.UpdatedAt
            })
            .ToList();

        return Json(new
        {
            timestamp = DateTime.UtcNow,
            stats = new
            {
                stats.TotalBooks,
                stats.TotalUsers,
                stats.AvgRating,
                stats.MonthlyReads,
                stats.GrowthPercent,
                stats.CompletionRate,
                queuedCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Queued, cancellationToken),
                runningCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Running, cancellationToken),
                failedCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Failed, cancellationToken),
                succeededCount = await _dbContext.ProcessingJobs.CountAsync(job => job.Status == ProcessingJobStatus.Succeeded, cancellationToken),
                adminCount = users.Count(user => user.Roles.Split(", ").Contains("Admin")),
                readerCount = users.Count(user => user.Roles.Split(", ").Contains("Reader")),
                uploadedBooksCount = users.Sum(user => user.UploadedBooksCount)
            },
            operations = new
            {
                operations.BooksProcessedToday,
                operations.Jobs24Hours,
                operations.JobsSucceeded24Hours,
                operations.JobsFailed24Hours,
                operations.JobSuccessRatePercent,
                operations.AverageJobDurationSeconds,
                operations.ModelCalls24Hours,
                operations.QuotaOrRateLimitErrors24Hours,
                operations.ModelSuccessRate24Hours,
                jobStatusMetrics = operations.JobStatusMetrics.Select(metric => new
                {
                    metric.Label,
                    metric.CssClass,
                    metric.Count,
                    metric.Percent
                }),
                modelQuotaMetrics = operations.ModelQuotaMetrics.Select(metric => new
                {
                    metric.Task,
                    metric.Model,
                    metric.TotalCount,
                    metric.SuccessCount,
                    metric.FailureCount,
                    metric.QuotaOrRateLimitFailureCount,
                    metric.SuccessRatePercent,
                    metric.PercentOfMax
                }),
                pipelineMetrics = operations.PipelineMetrics.Select(metric => new
                {
                    metric.Type,
                    metric.TotalCount,
                    metric.SucceededCount,
                    metric.FailedCount,
                    metric.RetryCount,
                    metric.SuccessRatePercent,
                    metric.AverageDurationSeconds,
                    metric.LatestFailure
                })
            },
            books = allBooks.Take(8).Select(book => new
            {
                book.Id,
                book.Title,
                book.Author,
                book.Rating,
                book.HasRating,
                book.ReviewCount,
                book.Status,
                book.Views
            }),
            allBooks = allBooks.Select(book => new
            {
                book.Id,
                book.Title,
                book.Author,
                book.Rating,
                book.HasRating,
                book.ReviewCount,
                book.Status,
                book.Source,
                book.Visibility,
                book.IsAudioReady,
                book.Views
            }),
            users = users.Select(user => new
            {
                user.Id,
                displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
                user.Email,
                user.Roles,
                user.UploadedBooksCount,
                user.BookmarksCount,
                user.ReadingProgressCount,
                user.CanDelete,
                user.DeleteUnavailableReason,
                createdAtText = user.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                lastLoginAtText = user.LastLoginAt.HasValue
                    ? user.LastLoginAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                    : "Chưa có"
            }),
            jobs = jobs.Select(job => new
            {
                job.Id,
                job.BookId,
                job.BookTitle,
                job.Type,
                job.Status,
                job.ProgressPercent,
                job.CurrentStep,
                job.ErrorMessage,
                job.RetryCount,
                job.CanRetry,
                job.QualityStatus,
                job.QualitySummary,
                job.QualityWarnings,
                updatedAt = job.UpdatedAt,
                updatedAtText = job.UpdatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            }),
            notifications = notifications.Select(notification => new
            {
                notification.Message,
                notification.Type,
                notification.IsRead,
                timestamp = notification.Timestamp
            }),
            activities = activities.Select(activity => new
            {
                activity.Action,
                activity.Detail,
                timestamp = activity.Timestamp
            })
        });
    }

    [HttpGet]
    [Route("Admin/Users")]
    public async Task<IActionResult> Users(CancellationToken cancellationToken)
    {
        var rows = await GetUserRowsAsync(cancellationToken);
        await ApplyUserDeletePermissionsAsync(rows);

        var viewModel = new AdminUsersPageViewModel
        {
            TotalUsers = rows.Count,
            AdminCount = rows.Count(user => user.Roles.Split(", ").Contains("Admin")),
            ReaderCount = rows.Count(user => user.Roles.Split(", ").Contains("Reader")),
            Users = rows
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/Users/Delete/{id}")]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.Equals(id, currentUserId, StringComparison.Ordinal))
        {
            TempData["AdminError"] = "Bạn không thể xoá chính tài khoản admin đang đăng nhập.";
            return RedirectToAction(nameof(Users));
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var currentUser = await _userManager.GetUserAsync(User);
        var targetIsAdmin = await _userManager.IsInRoleAsync(user, "Admin");

        if (IsDemoAdminUser(currentUser) && targetIsAdmin)
        {
            TempData["AdminError"] = "Tài khoản admin demo không được xoá tài khoản admin khác.";
            return RedirectToAction(nameof(Users));
        }

        if (targetIsAdmin)
        {
            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            if (admins.Count <= 1)
            {
                TempData["AdminError"] = "Không thể xoá admin cuối cùng của hệ thống.";
                return RedirectToAction(nameof(Users));
            }
        }

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? user.UserName ?? "tài khoản" : user.FullName;
        var uploadedBooks = await _dbContext.Books
            .Include(book => book.Files)
            .Include(book => book.Audios)
            .Where(book => book.OwnerUserId == id)
            .ToListAsync(cancellationToken);

        var relativePaths = uploadedBooks
            .SelectMany(book => book.Files.SelectMany(file => new[] { file.StoredFilePath, file.ExtractedTextPath })
                .Concat(book.Audios.Select(audio => audio.AudioUrl)))
            .Where(path => !string.IsNullOrWhiteSpace(path) && path.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .Distinct()
            .ToList();

        var avatarUrl = user.AvatarUrl;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (uploadedBooks.Count > 0)
        {
            _dbContext.Books.RemoveRange(uploadedBooks);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var deleteResult = await _userManager.DeleteAsync(user);
        if (!deleteResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            TempData["AdminError"] = BuildIdentityErrorMessage(deleteResult);
            return RedirectToAction(nameof(Users));
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var relativePath in relativePaths)
        {
            DeleteRelativeFile(relativePath);
        }

        DeleteLocalAvatar(avatarUrl);
        TempData["AdminSuccess"] = $"Đã xoá tài khoản \"{displayName}\".";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Admin/ProcessingJobs/Retry/{id:int}")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> RetryProcessingJob(int id, CancellationToken cancellationToken)
    {
        var job = await _dbContext.ProcessingJobs
            .Include(item => item.Book)
                .ThenInclude(book => book.Files)
            .Include(item => item.Book)
                .ThenInclude(book => book.ContentChunks)
            .Include(item => item.Book)
                .ThenInclude(book => book.ProcessingJobs)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (job is null)
        {
            return NotFound();
        }

        if (job.Status != ProcessingJobStatus.Failed)
        {
            TempData["AdminError"] = "Chỉ retry được job đang lỗi.";
            return RedirectToAction(nameof(ProcessingJobs));
        }

        var hasNewerReplacementJob = job.Book.ProcessingJobs.Any(item =>
            item.Id != job.Id &&
            item.Type == job.Type &&
            item.CreatedAt > job.CreatedAt &&
            item.Status != ProcessingJobStatus.Failed);

        if (hasNewerReplacementJob)
        {
            TempData["AdminError"] = "Job này đã có lượt xử lý mới hơn, không cần retry lại.";
            return RedirectToAction(nameof(ProcessingJobs));
        }

        var hasActiveJob = job.Book.ProcessingJobs.Any(item =>
            item.Id != job.Id &&
            (item.Status == ProcessingJobStatus.Queued || item.Status == ProcessingJobStatus.Running));

        if (hasActiveJob)
        {
            TempData["AdminError"] = "Sách này đang có job khác trong hàng đợi hoặc đang chạy.";
            return RedirectToAction(nameof(ProcessingJobs));
        }

        var nextType = ResolveRetryJobType(job);
        var now = DateTime.UtcNow;

        job.Book.ProcessingStatus = nextType switch
        {
            ProcessingJobType.ExtractText => BookProcessingStatus.Uploaded,
            ProcessingJobType.SummarizeBook => BookProcessingStatus.Extracted,
            ProcessingJobType.GenerateAudio => BookProcessingStatus.SummaryReady,
            _ => job.Book.ProcessingStatus
        };
        job.Book.FailedReason = null;
        job.Book.UpdatedAt = now;

        if (nextType == ProcessingJobType.ExtractText)
        {
            foreach (var file in job.Book.Files)
            {
                file.UploadStatus = BookFileUploadStatus.Uploaded;
            }
        }

        _dbContext.ProcessingJobs.Add(new ProcessingJob
        {
            BookId = job.BookId,
            UserId = job.UserId,
            Type = nextType,
            Status = ProcessingJobStatus.Queued,
            ProgressPercent = 0,
            RetryCount = job.RetryCount + 1,
            CurrentStep = nextType switch
            {
                ProcessingJobType.ExtractText => "Admin đã đưa sách vào hàng đợi trích xuất lại.",
                ProcessingJobType.SummarizeBook => "Admin đã đưa sách vào hàng đợi tóm tắt lại.",
                ProcessingJobType.GenerateAudio => "Admin đã đưa sách vào hàng đợi tạo audio lại.",
                _ => "Admin đã đưa sách vào hàng đợi xử lý lại."
            },
            CreatedAt = now,
            UpdatedAt = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        TempData["AdminSuccess"] = $"Đã retry job #{job.Id} cho \"{job.Book.Title}\".";
        return RedirectToAction(nameof(ProcessingJobs));
    }

    private async Task<AdminAiHealthViewModel> BuildAiHealthViewModelAsync(CancellationToken cancellationToken)
    {
        await RegisterConfiguredAiModelsAsync(cancellationToken);

        var models = (await _aiModelRouter.GetSnapshotAsync(cancellationToken))
            .Select(snapshot => new AiModelHealthRow
            {
                Task = FormatAiTask(snapshot.Task),
                Model = snapshot.Model,
                Status = snapshot.Status,
                SuccessCount = snapshot.SuccessCount,
                FailureCount = snapshot.FailureCount,
                SuccessRatePercent = snapshot.SuccessRatePercent,
                AverageDurationMilliseconds = snapshot.AverageDurationMilliseconds,
                QuotaOrRateLimitFailureCount = snapshot.QuotaFailureCount + snapshot.RateLimitFailureCount,
                LastFailureKind = snapshot.LastFailureKind,
                LastSuccessAt = snapshot.LastSuccessAt,
                LastFailureAt = snapshot.LastFailureAt,
                CooldownUntil = snapshot.CooldownUntil,
                LastError = snapshot.LastError
            })
            .OrderBy(model => model.Task)
            .ThenBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var operationsSince = DateTime.UtcNow.AddDays(-1);
        var operationMetrics = await _dbContext.AiModelOperationEvents
            .AsNoTracking()
            .Where(operation => operation.OccurredAt >= operationsSince)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Successful = group.Count(operation => operation.Succeeded),
                QuotaOrRateLimit = group.Count(operation =>
                    operation.FailureKind == "quota" || operation.FailureKind == "rate-limit")
            })
            .SingleOrDefaultAsync(cancellationToken);
        var recentOperations = await _dbContext.AiModelOperationEvents
            .AsNoTracking()
            .Include(operation => operation.Monitor)
            .OrderByDescending(operation => operation.OccurredAt)
            .Take(16)
            .Select(operation => new AiModelOperationRow
            {
                Task = operation.Monitor.Task,
                Model = operation.Monitor.Model,
                Succeeded = operation.Succeeded,
                DurationMilliseconds = operation.DurationMilliseconds,
                FailureKind = operation.FailureKind,
                ErrorMessage = operation.ErrorMessage,
                OccurredAt = operation.OccurredAt
            })
            .ToListAsync(cancellationToken);
        foreach (var operation in recentOperations)
        {
            operation.Task = Enum.TryParse<AiModelTask>(operation.Task, out var task)
                ? FormatAiTask(task)
                : operation.Task;
        }

        var recentJobs = await GetJobRowsAsync(12, cancellationToken);
        var pipelineEvents = await _dbContext.ProcessingJobs
            .AsNoTracking()
            .Where(job =>
                job.CreatedAt >= operationsSince &&
                (job.Type == ProcessingJobType.ExtractText ||
                 job.Type == ProcessingJobType.SummarizeBook ||
                 job.Type == ProcessingJobType.GenerateAudio))
            .Select(job => new
            {
                job.Type,
                job.Status,
                job.RetryCount,
                job.ErrorMessage,
                job.UpdatedAt,
                job.StartedAt,
                job.FinishedAt
            })
            .ToListAsync(cancellationToken);
        var pipelineJobs24Hours = pipelineEvents
            .GroupBy(job => job.Type)
            .Select(group =>
            {
                var totalCount = group.Count();
                var succeededCount = group.Count(job => job.Status == ProcessingJobStatus.Succeeded);
                var durations = group
                    .Where(job => job.StartedAt.HasValue && job.FinishedAt.HasValue)
                    .Select(job => (int)Math.Max(0, (job.FinishedAt!.Value - job.StartedAt!.Value).TotalSeconds))
                    .ToList();

                return new PipelineJobHealthRow
                {
                    Type = FormatProcessingJobType(group.Key),
                    TotalCount = totalCount,
                    SucceededCount = succeededCount,
                    FailedCount = group.Count(job => job.Status == ProcessingJobStatus.Failed),
                    RetryCount = group.Sum(job => job.RetryCount),
                    SuccessRatePercent = totalCount == 0
                        ? 0
                        : Math.Round(succeededCount * 100m / totalCount, 1),
                    AverageDurationSeconds = durations.Count == 0
                        ? null
                        : (int)Math.Round(durations.Average()),
                    LatestFailure = group
                        .Where(job => job.Status == ProcessingJobStatus.Failed && !string.IsNullOrWhiteSpace(job.ErrorMessage))
                        .OrderByDescending(job => job.UpdatedAt)
                        .Select(job => job.ErrorMessage)
                        .FirstOrDefault()
                };
            })
            .OrderBy(row => row.Type)
            .ToList();
        var recentAiFailures = await _dbContext.ProcessingJobs.CountAsync(job =>
            job.Status == ProcessingJobStatus.Failed &&
            (job.Type == ProcessingJobType.ExtractText ||
             job.Type == ProcessingJobType.SummarizeBook ||
             job.Type == ProcessingJobType.GenerateAudio) &&
            job.UpdatedAt >= DateTime.UtcNow.AddDays(-1),
            cancellationToken);

        return new AdminAiHealthViewModel
        {
            HealthyCount = models.Count(model => model.Status == "healthy"),
            CooldownCount = models.Count(model => model.Status == "cooldown"),
            WarningCount = models.Count(model => model.Status == "warning"),
            RecentAiFailures = recentAiFailures,
            ModelCalls24Hours = operationMetrics?.Count ?? 0,
            SuccessRate24Hours = operationMetrics?.Count > 0
                ? Math.Round((decimal)operationMetrics.Successful * 100m / operationMetrics.Count, 1)
                : 0,
            QuotaOrRateLimitErrors24Hours = operationMetrics?.QuotaOrRateLimit ?? 0,
            Models = models,
            RecentJobs = recentJobs,
            RecentOperations = recentOperations,
            PipelineJobs24Hours = pipelineJobs24Hours
        };
    }

    private async Task<AdminOperationsDashboardViewModel> BuildDashboardOperationsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-1);
        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();

        var pipelineTypes = new[]
        {
            ProcessingJobType.ExtractText,
            ProcessingJobType.SummarizeBook,
            ProcessingJobType.GenerateAudio
        };

        var jobEvents = await _dbContext.ProcessingJobs
            .AsNoTracking()
            .Where(job => pipelineTypes.Contains(job.Type))
            .Where(job => job.CreatedAt >= since || job.UpdatedAt >= since)
            .Select(job => new
            {
                job.BookId,
                job.Type,
                job.Status,
                job.RetryCount,
                job.ErrorMessage,
                job.CreatedAt,
                job.UpdatedAt,
                job.StartedAt,
                job.FinishedAt
            })
            .ToListAsync(cancellationToken);

        var booksProcessedToday = await _dbContext.ProcessingJobs
            .AsNoTracking()
            .Where(job => pipelineTypes.Contains(job.Type))
            .Where(job => job.Status == ProcessingJobStatus.Succeeded && job.UpdatedAt >= todayStartUtc)
            .Select(job => job.BookId)
            .Distinct()
            .CountAsync(cancellationToken);

        var jobCount = jobEvents.Count;
        var succeededJobCount = jobEvents.Count(job => job.Status == ProcessingJobStatus.Succeeded);
        var failedJobCount = jobEvents.Count(job => job.Status == ProcessingJobStatus.Failed);
        var jobDurations = jobEvents
            .Where(job => job.StartedAt.HasValue && (job.FinishedAt.HasValue ||
                job.Status is ProcessingJobStatus.Succeeded or ProcessingJobStatus.Failed or ProcessingJobStatus.Cancelled))
            .Select(job => (int)Math.Max(0, ((job.FinishedAt ?? job.UpdatedAt) - job.StartedAt!.Value).TotalSeconds))
            .ToList();

        var operationEvents = await _dbContext.AiModelOperationEvents
            .AsNoTracking()
            .Where(operation => operation.OccurredAt >= since)
            .Select(operation => new
            {
                operation.Succeeded,
                operation.FailureKind,
                operation.DurationMilliseconds,
                Task = operation.Monitor.Task,
                Model = operation.Monitor.Model
            })
            .ToListAsync(cancellationToken);

        var modelCalls = operationEvents.Count;
        var successfulModelCalls = operationEvents.Count(operation => operation.Succeeded);
        var quotaOrRateLimitErrors = operationEvents.Count(operation => IsQuotaOrRateLimitFailure(operation.FailureKind));
        var maxQuotaErrorsByModel = Math.Max(
            1,
            operationEvents
                .GroupBy(operation => new { operation.Task, operation.Model })
                .Select(group => group.Count(operation => IsQuotaOrRateLimitFailure(operation.FailureKind)))
                .DefaultIfEmpty(0)
                .Max());

        var modelQuotaMetrics = operationEvents
            .GroupBy(operation => new { operation.Task, operation.Model })
            .Select(group =>
            {
                var totalCount = group.Count();
                var successCount = group.Count(operation => operation.Succeeded);
                var quotaCount = group.Count(operation => IsQuotaOrRateLimitFailure(operation.FailureKind));
                var task = Enum.TryParse<AiModelTask>(group.Key.Task, out var parsedTask)
                    ? FormatAiTask(parsedTask)
                    : group.Key.Task;

                return new AdminModelQuotaMetric
                {
                    Task = task,
                    Model = group.Key.Model,
                    TotalCount = totalCount,
                    SuccessCount = successCount,
                    FailureCount = totalCount - successCount,
                    QuotaOrRateLimitFailureCount = quotaCount,
                    SuccessRatePercent = totalCount == 0
                        ? 0
                        : Math.Round(successCount * 100m / totalCount, 1),
                    PercentOfMax = Math.Round(quotaCount * 100m / maxQuotaErrorsByModel, 1)
                };
            })
            .OrderByDescending(metric => metric.QuotaOrRateLimitFailureCount)
            .ThenByDescending(metric => metric.FailureCount)
            .ThenByDescending(metric => metric.TotalCount)
            .Take(6)
            .ToList();

        var statusOrder = new[]
        {
            ProcessingJobStatus.Queued,
            ProcessingJobStatus.Running,
            ProcessingJobStatus.Succeeded,
            ProcessingJobStatus.Failed,
            ProcessingJobStatus.Cancelled
        };

        var jobStatusMetrics = statusOrder
            .Select(status =>
            {
                var count = jobEvents.Count(job => job.Status == status);
                return new AdminJobStatusMetric
                {
                    Label = FormatProcessingJobStatus(status),
                    CssClass = ToStatusCss(status),
                    Count = count,
                    Percent = jobCount == 0
                        ? 0
                        : Math.Round(count * 100m / jobCount, 1)
                };
            })
            .Where(metric => metric.Count > 0 || jobCount == 0)
            .ToList();

        var pipelineMetrics = jobEvents
            .GroupBy(job => job.Type)
            .Select(group =>
            {
                var totalCount = group.Count();
                var successCount = group.Count(job => job.Status == ProcessingJobStatus.Succeeded);
                var durations = group
                    .Where(job => job.StartedAt.HasValue && (job.FinishedAt.HasValue ||
                        job.Status is ProcessingJobStatus.Succeeded or ProcessingJobStatus.Failed or ProcessingJobStatus.Cancelled))
                    .Select(job => (int)Math.Max(0, ((job.FinishedAt ?? job.UpdatedAt) - job.StartedAt!.Value).TotalSeconds))
                    .ToList();

                return new PipelineJobHealthRow
                {
                    Type = FormatProcessingJobType(group.Key),
                    TotalCount = totalCount,
                    SucceededCount = successCount,
                    FailedCount = group.Count(job => job.Status == ProcessingJobStatus.Failed),
                    RetryCount = group.Sum(job => job.RetryCount),
                    SuccessRatePercent = totalCount == 0
                        ? 0
                        : Math.Round(successCount * 100m / totalCount, 1),
                    AverageDurationSeconds = durations.Count == 0
                        ? null
                        : (int)Math.Round(durations.Average()),
                    LatestFailure = group
                        .Where(job => job.Status == ProcessingJobStatus.Failed && !string.IsNullOrWhiteSpace(job.ErrorMessage))
                        .OrderByDescending(job => job.UpdatedAt)
                        .Select(job => job.ErrorMessage)
                        .FirstOrDefault()
                };
            })
            .OrderBy(row => row.Type)
            .ToList();

        return new AdminOperationsDashboardViewModel
        {
            BooksProcessedToday = booksProcessedToday,
            Jobs24Hours = jobCount,
            JobsSucceeded24Hours = succeededJobCount,
            JobsFailed24Hours = failedJobCount,
            JobSuccessRatePercent = jobCount == 0
                ? 0
                : Math.Round(succeededJobCount * 100m / jobCount, 1),
            AverageJobDurationSeconds = jobDurations.Count == 0
                ? null
                : (int)Math.Round(jobDurations.Average()),
            ModelCalls24Hours = modelCalls,
            QuotaOrRateLimitErrors24Hours = quotaOrRateLimitErrors,
            ModelSuccessRate24Hours = modelCalls == 0
                ? 0
                : Math.Round(successfulModelCalls * 100m / modelCalls, 1),
            JobStatusMetrics = jobStatusMetrics,
            ModelQuotaMetrics = modelQuotaMetrics,
            PipelineMetrics = pipelineMetrics
        };
    }

    private async Task RegisterConfiguredAiModelsAsync(CancellationToken cancellationToken)
    {
        await _aiModelRouter.RegisterModelsAsync(AiModelTask.Summarization, BuildGeminiTextModels(), cancellationToken);
        await _aiModelRouter.RegisterModelsAsync(AiModelTask.Chat, BuildGeminiChatModels(), cancellationToken);
        await _aiModelRouter.RegisterModelsAsync(AiModelTask.Ocr, BuildGeminiTextModels(), cancellationToken);
        await _aiModelRouter.RegisterModelsAsync(AiModelTask.TextToSpeech, BuildGeminiTtsModels(), cancellationToken);
    }

    private IEnumerable<string> BuildGeminiTextModels()
    {
        foreach (var model in _aiOptions.Gemini.Models)
        {
            yield return model;
        }

        yield return _aiOptions.Gemini.Model;
    }

    private IEnumerable<string> BuildGeminiChatModels()
    {
        foreach (var model in _aiOptions.Gemini.ChatModels)
        {
            yield return model;
        }

        yield return _aiOptions.Gemini.ChatModel;
        yield return _aiOptions.Gemini.Model;
    }

    private IEnumerable<string> BuildGeminiTtsModels()
    {
        foreach (var model in _audioOptions.Gemini.Models)
        {
            yield return model;
        }

        yield return _audioOptions.Gemini.Model;
    }

    private static string FormatAiTask(AiModelTask task)
    {
        return task switch
        {
            AiModelTask.Chat => "Chat",
            AiModelTask.Summarization => "Tóm tắt",
            AiModelTask.Ocr => "OCR",
            AiModelTask.TextToSpeech => "TTS",
            _ => task.ToString()
        };
    }

    private static string FormatProcessingJobStatus(ProcessingJobStatus status)
    {
        return status switch
        {
            ProcessingJobStatus.Queued => "Đang chờ",
            ProcessingJobStatus.Running => "Đang chạy",
            ProcessingJobStatus.Succeeded => "Thành công",
            ProcessingJobStatus.Failed => "Thất bại",
            ProcessingJobStatus.Cancelled => "Đã hủy",
            _ => status.ToString()
        };
    }

    private static bool IsQuotaOrRateLimitFailure(string? failureKind)
    {
        return string.Equals(failureKind, "quota", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(failureKind, "rate-limit", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNullableDate(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
            : "Chưa có";
    }

    private async Task<List<BookRow>> GetBookRowsAsync(int? take, CancellationToken cancellationToken)
    {
        var query = _dbContext.Books
            .AsNoTracking()
            .OrderByDescending(book => book.CreatedAt)
            .Select(book => new
            {
                book.Id,
                book.Title,
                book.AuthorName,
                Rating = book.Reviews.Select(review => (decimal?)review.Rating).Average() ?? 0m,
                ReviewCount = book.Reviews.Count,
                book.ProcessingStatus,
                book.SourceType,
                book.Visibility,
                book.IsAudioReady,
                book.CreatedAt,
                Interactions = book.Bookmarks.Count +
                    book.ReadingProgressEntries.Count +
                    book.Reviews.Count +
                    book.ChatSessions.Count
            });

        if (take is > 0)
        {
            query = query.Take(take.Value);
        }

        var books = await query.ToListAsync(cancellationToken);

        return books.Select(book => new BookRow
        {
            Id = book.Id,
            Title = book.Title,
            Author = book.AuthorName,
            Rating = book.Rating,
            HasRating = book.ReviewCount > 0,
            ReviewCount = book.ReviewCount,
            Status = ToBookStatus(book.ProcessingStatus, book.Visibility),
            Source = book.SourceType.ToString(),
            Visibility = book.Visibility.ToString(),
            IsAudioReady = book.IsAudioReady,
            DateAdded = book.CreatedAt,
            Views = book.Interactions
        }).ToList();
    }

    private async Task<DashboardStats> BuildDashboardStatsAsync(CancellationToken cancellationToken)
    {
        var totalBooks = await _dbContext.Books.CountAsync(cancellationToken);
        var totalUsers = await _dbContext.Users.CountAsync(cancellationToken);
        var readyBooks = await _dbContext.Books.CountAsync(book =>
            book.ProcessingStatus == BookProcessingStatus.Ready ||
            book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
            book.ProcessingStatus == BookProcessingStatus.GeneratingAudio,
            cancellationToken);
        var audioReadyCount = await _dbContext.Books.CountAsync(book => book.IsAudioReady, cancellationToken);
        var recentCutoff = DateTime.UtcNow.AddDays(-30);
        var recentReadingProgressCount = await _dbContext.ReadingProgressEntries
            .CountAsync(progress => progress.UpdatedAt >= recentCutoff, cancellationToken);
        var hasReviews = await _dbContext.BookReviews.AnyAsync(cancellationToken);
        var averageReviewRating = hasReviews
            ? await _dbContext.BookReviews.AverageAsync(review => (decimal)review.Rating, cancellationToken)
            : 0m;

        return new DashboardStats
        {
            TotalBooks = totalBooks,
            TotalUsers = totalUsers,
            AvgRating = Math.Round(averageReviewRating, 2),
            MonthlyReads = recentReadingProgressCount,
            GrowthPercent = Percent(audioReadyCount, totalBooks),
            CompletionRate = Percent(readyBooks, totalBooks)
        };
    }

    private async Task<List<AdminUserRow>> GetUserRowsAsync(CancellationToken cancellationToken)
    {
        var roleLookup = await (
            from userRole in _dbContext.UserRoles
            join role in _dbContext.Roles on userRole.RoleId equals role.Id
            group role.Name by userRole.UserId into roleGroup
            select new
            {
                UserId = roleGroup.Key,
                Roles = roleGroup.Where(role => role != null).Select(role => role!).ToList()
            })
            .ToDictionaryAsync(item => item.UserId, item => item.Roles, cancellationToken);

        var users = await _dbContext.Users
            .AsNoTracking()
            .OrderByDescending(user => user.CreatedAt)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.Email,
                user.CreatedAt,
                user.LastLoginAt,
                UploadedBooksCount = user.UploadedBooks.Count,
                BookmarksCount = user.Bookmarks.Count,
                ReadingProgressCount = user.ReadingProgressEntries.Count
            })
            .ToListAsync(cancellationToken);

        return users.Select(user =>
        {
            roleLookup.TryGetValue(user.Id, out var roles);
            var roleText = roles is { Count: > 0 }
                ? string.Join(", ", roles.OrderBy(role => role))
                : "Reader";

            return new AdminUserRow
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email ?? string.Empty,
                Roles = roleText,
                UploadedBooksCount = user.UploadedBooksCount,
                BookmarksCount = user.BookmarksCount,
                ReadingProgressCount = user.ReadingProgressCount,
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt
            };
        }).ToList();
    }

    private async Task ApplyUserDeletePermissionsAsync(List<AdminUserRow> users)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var currentUser = await _userManager.GetUserAsync(User);
        var currentUserIsDemoAdmin = IsDemoAdminUser(currentUser);

        foreach (var user in users)
        {
            if (string.Equals(user.Id, currentUserId, StringComparison.Ordinal))
            {
                user.CanDelete = false;
                user.DeleteUnavailableReason = "Bạn";
                continue;
            }

            if (currentUserIsDemoAdmin && HasAdminRole(user))
            {
                user.CanDelete = false;
                user.DeleteUnavailableReason = "Admin được bảo vệ";
                continue;
            }

            user.CanDelete = true;
            user.DeleteUnavailableReason = string.Empty;
        }
    }

    private bool IsDemoAdminUser(ApplicationUser? user)
    {
        var demoAdminEmail = _configuration["SeedDemoAdmin:Email"] ?? "demo.admin@lemonink.local";
        return !string.IsNullOrWhiteSpace(user?.Email)
            && string.Equals(user.Email, demoAdminEmail, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdminRole(AdminUserRow user)
    {
        return user.Roles
            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Contains("Admin", StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<ProcessingJobRow>> GetJobRowsAsync(int? take, CancellationToken cancellationToken)
    {
        var query = _dbContext.ProcessingJobs
            .AsNoTracking()
            .OrderByDescending(job => job.UpdatedAt)
            .Select(job => new
            {
                job.Id,
                job.BookId,
                BookTitle = job.Book.Title,
                job.Type,
                job.Status,
                job.ProgressPercent,
                job.CurrentStep,
                job.ErrorMessage,
                job.RetryCount,
                job.CreatedAt,
                job.UpdatedAt,
                job.StartedAt,
                job.FinishedAt,
                Quality = job.QualityReports
                    .OrderByDescending(report => report.CreatedAt)
                    .Select(report => new
                    {
                        report.Status,
                        report.EstimatedPageCount,
                        report.SourceChunkCount,
                        report.DetectedChapterCount,
                        report.ExpectedChapterCount,
                        report.CoveredChapterCount,
                        report.SummaryWordCount,
                        report.AudioCoveragePercent,
                        report.WarningsJson
                    })
                    .FirstOrDefault(),
                HasNewerReplacementJob = job.Book.ProcessingJobs.Any(item =>
                    item.Id != job.Id &&
                    item.Type == job.Type &&
                    item.CreatedAt > job.CreatedAt &&
                    item.Status != ProcessingJobStatus.Failed)
            });

        if (take is > 0)
        {
            query = query.Take(take.Value);
        }

        var jobs = await query.ToListAsync(cancellationToken);

        return jobs.Select(job => new ProcessingJobRow
        {
            Id = job.Id,
            BookId = job.BookId,
            BookTitle = job.BookTitle,
            Type = job.Type.ToString(),
            Status = ToStatusCss(job.Status),
            ProgressPercent = job.ProgressPercent,
            CurrentStep = job.CurrentStep,
            ErrorMessage = job.ErrorMessage,
            RetryCount = job.RetryCount,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            DurationSeconds = job.StartedAt.HasValue
                ? (int)Math.Max(0, ((job.FinishedAt ?? job.UpdatedAt) - job.StartedAt.Value).TotalSeconds)
                : null,
            QualityStatus = job.Quality?.Status.ToString().ToLowerInvariant(),
            QualitySummary = job.Quality is null
                ? null
                : BuildQualitySummary(
                    job.Quality.EstimatedPageCount,
                    job.Quality.SourceChunkCount,
                    job.Quality.CoveredChapterCount,
                    job.Quality.ExpectedChapterCount,
                    job.Quality.DetectedChapterCount,
                    job.Quality.AudioCoveragePercent,
                    job.Quality.SummaryWordCount),
            QualityWarnings = job.Quality is null ? null : FormatQualityWarnings(job.Quality.WarningsJson),
            CanRetry = job.Status == ProcessingJobStatus.Failed && !job.HasNewerReplacementJob
        }).ToList();
    }

    private static string BuildQualitySummary(
        int? estimatedPageCount,
        int sourceChunkCount,
        int coveredChapterCount,
        int expectedChapterCount,
        int detectedChapterCount,
        decimal? audioCoveragePercent,
        int summaryWordCount)
    {
        var parts = new List<string>();

        if (estimatedPageCount is > 0)
        {
            parts.Add($"~{estimatedPageCount.Value} trang");
        }

        if (sourceChunkCount > 0)
        {
            parts.Add($"{sourceChunkCount} chunk");
        }

        if (expectedChapterCount > 0)
        {
            parts.Add($"{coveredChapterCount}/{expectedChapterCount} chương");
        }
        else if (detectedChapterCount > 0)
        {
            parts.Add($"{detectedChapterCount} chương");
        }

        if (summaryWordCount > 0)
        {
            parts.Add($"{summaryWordCount:N0} từ tóm tắt");
        }

        if (audioCoveragePercent.HasValue)
        {
            parts.Add($"audio {audioCoveragePercent.Value:0.#}%");
        }

        return parts.Count == 0 ? "Đã ghi nhận báo cáo chất lượng." : string.Join(" · ", parts);
    }

    private static string? FormatQualityWarnings(string? warningsJson)
    {
        if (string.IsNullOrWhiteSpace(warningsJson))
        {
            return null;
        }

        try
        {
            var warnings = JsonSerializer.Deserialize<List<string>>(warningsJson) ?? new List<string>();
            return warnings.Count == 0 ? null : string.Join(" ", warnings.Take(2));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ProcessingJobType ResolveRetryJobType(ProcessingJob job)
    {
        if (job.Type == ProcessingJobType.GenerateAudio && job.Book.IsSummaryReady)
        {
            return ProcessingJobType.GenerateAudio;
        }

        if (job.Type == ProcessingJobType.SummarizeBook && job.Book.ContentChunks.Count > 0)
        {
            return ProcessingJobType.SummarizeBook;
        }

        return job.Book.ContentChunks.Count > 0
            ? ProcessingJobType.SummarizeBook
            : ProcessingJobType.ExtractText;
    }

    private static bool HasActiveJob(Book book)
    {
        return book.ProcessingJobs.Any(job =>
            job.Status == ProcessingJobStatus.Queued ||
            job.Status == ProcessingJobStatus.Running);
    }

    private AdminCreateCuratedBookViewModel ToCuratedForm(Book book)
    {
        return new AdminCreateCuratedBookViewModel
        {
            Id = book.Id,
            Title = book.Title,
            AuthorName = book.AuthorName,
            Category = book.Category,
            Description = book.Description ?? string.Empty,
            Introduction = book.Introduction,
            Rating = book.Rating ?? 0m,
            ReadingTimeMinutes = Math.Max(1, book.ReadingTimeMinutes),
            CoverUrl = book.CoverUrl,
            CoverGradient = book.CoverGradient,
            TakeawaysText = string.Join(
                Environment.NewLine,
                book.Takeaways
                    .OrderBy(item => item.SortOrder)
                    .Select(item => $"- {item.Content}")),
            ChaptersText = string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                book.SummarySections
                    .Where(item => item.SectionType == SummarySectionType.Chapter)
                    .OrderBy(item => item.SortOrder)
                    .Select(item => $"{item.Title}{Environment.NewLine}{StripHtml(item.ContentHtml)}")),
            PublishImmediately = book.Visibility == BookVisibility.Public
        };
    }

    private async Task ApplyCuratedFormToExistingBookAsync(
        Book book,
        AdminCreateCuratedBookViewModel form,
        CancellationToken cancellationToken)
    {
        var chapters = ParseChapters(form.ChaptersText);
        var takeaways = ParseTakeaways(form.TakeawaysText);
        var now = DateTime.UtcNow;
        var summaryText = NormalizeText(form.Introduction);

        book.Title = form.Title.Trim();
        book.AuthorName = form.AuthorName.Trim();
        book.Description = form.Description.Trim();
        book.Introduction = summaryText;
        book.Category = form.Category.Trim();
        book.CoverUrl = string.IsNullOrWhiteSpace(form.CoverUrl)
            ? await _coverService.EnsureCoverAsync(book, cancellationToken)
            : form.CoverUrl.Trim();
        book.CoverGradient = string.IsNullOrWhiteSpace(form.CoverGradient)
            ? _coverService.BuildGradient(book)
            : form.CoverGradient.Trim();
        book.Visibility = form.PublishImmediately ? BookVisibility.Public : BookVisibility.Unlisted;
        book.ProcessingStatus = BookProcessingStatus.SummaryReady;
        book.Language = "vi";
        book.Rating = Math.Clamp(form.Rating, 0, 10);
        book.ReadingTimeMinutes = Math.Max(1, form.ReadingTimeMinutes);
        book.IsSummaryReady = true;
        book.IsAudioReady = false;
        book.AudioDurationSeconds = null;
        book.FailedReason = null;
        book.PublishedAt = form.PublishImmediately ? book.PublishedAt ?? now : null;
        book.UpdatedAt = now;

        _dbContext.GeneratedBookSummaries.RemoveRange(book.GeneratedSummaries);
        _dbContext.BookTakeaways.RemoveRange(book.Takeaways);
        _dbContext.BookSummarySections.RemoveRange(book.SummarySections);
        _dbContext.BookContentChunks.RemoveRange(book.ContentChunks);
        _dbContext.BookAudios.RemoveRange(book.Audios);

        book.GeneratedSummaries = new List<GeneratedBookSummary>
        {
            new()
            {
                BookId = book.Id,
                ShortSummary = form.Description.Trim(),
                LongSummary = summaryText,
                GeneratedBy = "LemonInk admin",
                EditedByAdmin = true,
                GeneratedAt = now,
                UpdatedAt = now
            }
        };

        book.Takeaways = takeaways
            .Select((content, index) => new BookTakeaway
            {
                BookId = book.Id,
                Content = content,
                SortOrder = index + 1
            })
            .ToList();

        book.SummarySections = chapters
            .Select((chapter, index) =>
            {
                var chapterNumber = index + 1;
                return new BookSummarySection
                {
                    BookId = book.Id,
                    SectionType = SummarySectionType.Chapter,
                    ChapterNumber = chapterNumber,
                    Title = chapter.Title,
                    ContentHtml = chapter.ContentHtml,
                    ReadingTimeMinutes = chapter.ReadingTimeMinutes,
                    SortOrder = chapterNumber,
                    CreatedAt = now,
                    UpdatedAt = now
                };
            })
            .ToList();

        book.ContentChunks = chapters
            .Select((chapter, index) => new BookContentChunk
            {
                BookId = book.Id,
                ChunkIndex = index,
                Content = chapter.PlainText,
                TokenCount = EstimateTokenCount(chapter.PlainText),
                CreatedAt = now
            })
            .ToList();
    }

    private void ValidateCuratedBookForm(AdminCreateCuratedBookViewModel form)
    {
        if (string.IsNullOrWhiteSpace(form.Title))
        {
            ModelState.AddModelError(nameof(form.Title), "Nhập tên sách.");
        }

        if (string.IsNullOrWhiteSpace(form.AuthorName))
        {
            ModelState.AddModelError(nameof(form.AuthorName), "Nhập tên tác giả.");
        }

        if (string.IsNullOrWhiteSpace(form.Category))
        {
            ModelState.AddModelError(nameof(form.Category), "Nhập thể loại.");
        }

        if (string.IsNullOrWhiteSpace(form.Description))
        {
            ModelState.AddModelError(nameof(form.Description), "Nhập mô tả ngắn.");
        }

        if (string.IsNullOrWhiteSpace(form.Introduction))
        {
            ModelState.AddModelError(nameof(form.Introduction), "Nhập tóm tắt tổng quan.");
        }

        if (ParseTakeaways(form.TakeawaysText).Count == 0)
        {
            ModelState.AddModelError(nameof(form.TakeawaysText), "Nhập ít nhất một takeaway.");
        }

        if (ParseChapters(form.ChaptersText).Count == 0)
        {
            ModelState.AddModelError(nameof(form.ChaptersText), "Nhập ít nhất một chương.");
        }
    }

    private async Task<string> BuildUniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(title);
        var slug = baseSlug;
        var suffix = 2;

        while (await _dbContext.Books.AnyAsync(book => book.Slug == slug, cancellationToken))
        {
            slug = $"{baseSlug}-{suffix}";
            suffix += 1;
        }

        return slug;
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
        return string.IsNullOrWhiteSpace(slug) ? $"sach-{Guid.NewGuid():N}"[..13] : slug;
    }

    private static List<string> ParseTakeaways(string value)
    {
        return SplitLines(value)
            .Select(line => line.TrimStart('-', '*', '•').Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(12)
            .ToList();
    }

    private static List<CuratedChapterDraft> ParseChapters(string value)
    {
        return Regex.Split(value.Replace("\r\n", "\n").Trim(), "\n{2,}")
            .Select(ParseChapterBlock)
            .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title) && !string.IsNullOrWhiteSpace(chapter.PlainText))
            .Take(20)
            .ToList();
    }

    private static CuratedChapterDraft ParseChapterBlock(string block)
    {
        var lines = SplitLines(block).ToList();
        if (lines.Count == 0)
        {
            return new CuratedChapterDraft(string.Empty, string.Empty, string.Empty, 1);
        }

        var title = lines[0].Trim().TrimStart('#').Trim();
        var bodyLines = lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (bodyLines.Count == 0)
        {
            bodyLines.Add(title);
        }

        var plainText = NormalizeText(string.Join("\n\n", bodyLines));
        var contentHtml = string.Join(
            Environment.NewLine,
            Regex.Split(plainText, "\n{2,}")
                .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
                .Select(paragraph => $"<p>{WebUtility.HtmlEncode(paragraph.Trim())}</p>"));

        return new CuratedChapterDraft(
            title,
            plainText,
            contentHtml,
            Math.Max(1, (int)Math.Ceiling(plainText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 220m)));
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        return value
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeText(string value)
    {
        return Regex.Replace(value.Replace("\r\n", "\n").Trim(), "[ \t]+", " ");
    }

    private static string StripHtml(string value)
    {
        return WebUtility.HtmlDecode(Regex.Replace(value, "<.*?>", " "))
            .Replace("&nbsp;", " ")
            .Trim();
    }

    private static int EstimateTokenCount(string value)
    {
        return Math.Max(1, value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length);
    }

    private void DeleteRelativeFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !relativePath.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var contentRoot = Path.GetFullPath(_environment.ContentRootPath);
        var physicalPath = Path.GetFullPath(Path.Combine(contentRoot, relativePath));
        if (!physicalPath.StartsWith(contentRoot, StringComparison.Ordinal) ||
            !System.IO.File.Exists(physicalPath))
        {
            return;
        }

        System.IO.File.Delete(physicalPath);
    }

    private void DeleteLocalAvatar(string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl) ||
            !avatarUrl.StartsWith("/uploads/avatars/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetFileName(avatarUrl);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        var webRoot = Path.GetFullPath(_environment.WebRootPath);
        var physicalPath = Path.GetFullPath(Path.Combine(webRoot, "uploads", "avatars", fileName));
        if (!physicalPath.StartsWith(webRoot, StringComparison.Ordinal) ||
            !System.IO.File.Exists(physicalPath))
        {
            return;
        }

        System.IO.File.Delete(physicalPath);
    }

    private static string BuildIdentityErrorMessage(IdentityResult result)
    {
        var errors = result.Errors
            .Select(error => error.Description)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .ToList();

        return errors.Count == 0
            ? "Không thể xoá tài khoản này."
            : string.Join(" ", errors);
    }

    private static List<Notification> BuildNotifications(
        IReadOnlyList<ProcessingJobRow> failedJobs,
        IReadOnlyList<ProcessingJobRow> runningJobs,
        int audioReadyCount,
        int totalBooks)
    {
        var notifications = new List<Notification>();

        notifications.AddRange(failedJobs.Take(3).Select(job => new Notification
        {
            Message = $"Job {job.Type} cho \"{job.BookTitle}\" đang lỗi: {job.ErrorMessage ?? job.CurrentStep}",
            Type = "error",
            Timestamp = job.UpdatedAt,
            IsRead = false
        }));

        notifications.AddRange(runningJobs.Take(2).Select(job => new Notification
        {
            Message = $"Job {job.Type} cho \"{job.BookTitle}\" đang chạy ở {job.ProgressPercent}%.",
            Type = "info",
            Timestamp = job.UpdatedAt,
            IsRead = false
        }));

        if (notifications.Count == 0)
        {
            notifications.Add(new Notification
            {
                Message = totalBooks == 0
                    ? "Chưa có sách trong hệ thống."
                    : $"Có {audioReadyCount}/{totalBooks} sách đã có audio sẵn sàng.",
                Type = audioReadyCount == totalBooks && totalBooks > 0 ? "success" : "info",
                Timestamp = DateTime.UtcNow,
                IsRead = true
            });
        }

        return notifications;
    }

    private static string ToBookStatus(BookProcessingStatus processingStatus, BookVisibility visibility)
    {
        if (processingStatus == BookProcessingStatus.Failed)
        {
            return "failed";
        }

        if (visibility == BookVisibility.Public &&
            (processingStatus == BookProcessingStatus.Ready ||
             processingStatus == BookProcessingStatus.SummaryReady ||
             processingStatus == BookProcessingStatus.GeneratingAudio))
        {
            return "published";
        }

        if (processingStatus == BookProcessingStatus.Draft ||
            processingStatus == BookProcessingStatus.Uploaded)
        {
            return "draft";
        }

        return "processing";
    }

    private static string ToStatusCss(ProcessingJobStatus status)
    {
        return status.ToString().ToLowerInvariant();
    }

    private static string FormatProcessingJobType(ProcessingJobType type)
    {
        return type switch
        {
            ProcessingJobType.ExtractText => "Trích xuất / OCR",
            ProcessingJobType.SummarizeBook => "Tạo tóm tắt",
            ProcessingJobType.GenerateAudio => "Tạo audio",
            ProcessingJobType.BuildChatIndex => "Lập chỉ mục chat",
            _ => type.ToString()
        };
    }

    private static decimal Percent(int value, int total)
    {
        return total == 0 ? 0 : Math.Round(value * 100m / total, 2);
    }

    private sealed record CuratedChapterDraft(string Title, string PlainText, string ContentHtml, int ReadingTimeMinutes);
}
