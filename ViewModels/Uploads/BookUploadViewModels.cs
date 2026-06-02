using Microsoft.AspNetCore.Http;
using ZenRead.Entities;

namespace ZenRead.ViewModels;

public class BookUploadFormViewModel
{
    public string Title { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    public string Category { get; set; } = "Sách cá nhân";

    public string Language { get; set; } = "vi";

    public IFormFile? File { get; set; }

    public List<UserUploadBookItem> RecentUploads { get; set; } = new();
}

public class UserUploadBookItem
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public BookProcessingStatus ProcessingStatus { get; set; }

    public ProcessingJobStatus? LatestJobStatus { get; set; }

    public int LatestJobProgress { get; set; }

    public string LatestJobStep { get; set; } = string.Empty;

    public int LatestJobRetryCount { get; set; }

    public DateTime? LatestJobNextRunAt { get; set; }

    public DateTime? LatestJobStartedAt { get; set; }

    public int OverallProgressPercent { get; set; }

    public string EstimatedTimeText { get; set; } = string.Empty;

    public string? FailedReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public int ExtractedCharacterCount { get; set; }

    public int SummaryContentCharacterCount { get; set; }

    public int CompletedSummaryPassCount { get; set; }

    public int SummaryRequestDurationSeconds { get; set; }
}

public class BookUploadResult
{
    public bool Succeeded { get; set; }

    public int? BookId { get; set; }

    public string Message { get; set; } = string.Empty;
}
