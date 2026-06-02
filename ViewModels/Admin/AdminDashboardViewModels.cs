using System.Globalization;

namespace ZenRead.ViewModels;

public class AdminDashboardViewModel
{
    public DashboardStats Stats { get; set; } = new();
    public AdminOperationsDashboardViewModel Operations { get; set; } = new();
    public List<BookRow> Books { get; set; } = new();
    public List<Notification> Notifications { get; set; } = new();
    public List<RecentActivity> RecentActivities { get; set; } = new();
    public List<ProcessingJobRow> Jobs { get; set; } = new();
}

public class DashboardStats
{
    public int TotalBooks { get; set; }
    public int TotalUsers { get; set; }
    public decimal AvgRating { get; set; }
    public int MonthlyReads { get; set; }
    public decimal GrowthPercent { get; set; }
    public decimal CompletionRate { get; set; }

    // Format helpers — always 2 decimal places
    public string AvgRatingFormatted => AvgRating.ToString("F2", CultureInfo.InvariantCulture);
    public string GrowthPercentFormatted => GrowthPercent.ToString("F2", CultureInfo.InvariantCulture);
    public string CompletionRateFormatted => CompletionRate.ToString("F2", CultureInfo.InvariantCulture);
}

public class BookRow
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public decimal Rating { get; set; }
    public bool HasRating { get; set; }
    public int ReviewCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsAudioReady { get; set; }
    public DateTime DateAdded { get; set; }
    public int Views { get; set; }

    // Format helpers
    public string RatingFormatted => Rating.ToString("F2", CultureInfo.InvariantCulture);
}

public class ProcessingJobRow
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string BookTitle { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool CanRetry { get; set; }
    public int? DurationSeconds { get; set; }
    public string? QualityStatus { get; set; }
    public string? QualitySummary { get; set; }
    public string? QualityWarnings { get; set; }
}

public class AdminOperationsDashboardViewModel
{
    public int BooksProcessedToday { get; set; }
    public int Jobs24Hours { get; set; }
    public int JobsSucceeded24Hours { get; set; }
    public int JobsFailed24Hours { get; set; }
    public decimal JobSuccessRatePercent { get; set; }
    public int? AverageJobDurationSeconds { get; set; }
    public int ModelCalls24Hours { get; set; }
    public int QuotaOrRateLimitErrors24Hours { get; set; }
    public decimal ModelSuccessRate24Hours { get; set; }
    public List<AdminJobStatusMetric> JobStatusMetrics { get; set; } = new();
    public List<AdminModelQuotaMetric> ModelQuotaMetrics { get; set; } = new();
    public List<PipelineJobHealthRow> PipelineMetrics { get; set; } = new();
}

public class AdminJobStatusMetric
{
    public string Label { get; set; } = string.Empty;
    public string CssClass { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Percent { get; set; }
}

public class AdminModelQuotaMetric
{
    public string Task { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int QuotaOrRateLimitFailureCount { get; set; }
    public decimal SuccessRatePercent { get; set; }
    public decimal PercentOfMax { get; set; }
}

public class AdminBooksPageViewModel
{
    public List<BookRow> Books { get; set; } = new();
}

public class AdminCreateCuratedBookViewModel
{
    public int? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Introduction { get; set; } = string.Empty;
    public decimal Rating { get; set; } = 8.0m;
    public int ReadingTimeMinutes { get; set; } = 15;
    public string? CoverUrl { get; set; }
    public string CoverGradient { get; set; } = "linear-gradient(135deg, #f8bb16 0%, #38bdf8 100%)";
    public string TakeawaysText { get; set; } = string.Empty;
    public string ChaptersText { get; set; } = string.Empty;
    public bool PublishImmediately { get; set; } = true;
    public bool IsEdit => Id.HasValue;
}

public class AdminProcessingJobsPageViewModel
{
    public int QueuedCount { get; set; }
    public int RunningCount { get; set; }
    public int FailedCount { get; set; }
    public int SucceededCount { get; set; }
    public List<ProcessingJobRow> Jobs { get; set; } = new();
}

public class AdminAiHealthViewModel
{
    public int HealthyCount { get; set; }

    public int CooldownCount { get; set; }

    public int WarningCount { get; set; }

    public int RecentAiFailures { get; set; }

    public int ModelCalls24Hours { get; set; }

    public decimal SuccessRate24Hours { get; set; }

    public int QuotaOrRateLimitErrors24Hours { get; set; }

    public List<AiModelHealthRow> Models { get; set; } = new();

    public List<ProcessingJobRow> RecentJobs { get; set; } = new();

    public List<AiModelOperationRow> RecentOperations { get; set; } = new();

    public List<PipelineJobHealthRow> PipelineJobs24Hours { get; set; } = new();
}

public class AiModelHealthRow
{
    public string Task { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Status { get; set; } = "healthy";

    public long SuccessCount { get; set; }

    public long FailureCount { get; set; }

    public decimal SuccessRatePercent { get; set; }

    public long AverageDurationMilliseconds { get; set; }

    public long QuotaOrRateLimitFailureCount { get; set; }

    public string? LastFailureKind { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime? LastFailureAt { get; set; }

    public DateTime? CooldownUntil { get; set; }

    public string? LastError { get; set; }
}

public class AiModelOperationRow
{
    public string Task { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public int DurationMilliseconds { get; set; }
    public string? FailureKind { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class PipelineJobHealthRow
{
    public string Type { get; set; } = string.Empty;
    public int TotalCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int RetryCount { get; set; }
    public decimal SuccessRatePercent { get; set; }
    public int? AverageDurationSeconds { get; set; }
    public string? LatestFailure { get; set; }
}

public class AdminUsersPageViewModel
{
    public int TotalUsers { get; set; }
    public int AdminCount { get; set; }
    public int ReaderCount { get; set; }
    public List<AdminUserRow> Users { get; set; } = new();
}

public class AdminUserRow
{
    public string Id { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Roles { get; set; } = string.Empty;
    public int UploadedBooksCount { get; set; }
    public int BookmarksCount { get; set; }
    public int ReadingProgressCount { get; set; }
    public bool CanDelete { get; set; }
    public string DeleteUnavailableReason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public class Notification
{
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = "info"; // info, success, warning, error
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsRead { get; set; }
}

public class RecentActivity
{
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
