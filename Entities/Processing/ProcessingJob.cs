namespace ZenRead.Entities;

public enum ProcessingJobType
{
    ExtractText = 1,
    SummarizeBook = 2,
    GenerateAudio = 3,
    BuildChatIndex = 4
}

public enum ProcessingJobStatus
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}

public class ProcessingJob
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string? UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public ProcessingJobType Type { get; set; }

    public ProcessingJobStatus Status { get; set; } = ProcessingJobStatus.Queued;

    public int ProgressPercent { get; set; }

    public string CurrentStep { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public DateTime? NextRunAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<BookProcessingQualityReport> QualityReports { get; set; } = new();
}
