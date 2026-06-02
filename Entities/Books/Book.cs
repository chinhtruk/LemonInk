namespace ZenRead.Entities;

public enum BookSourceType
{
    Curated = 1,
    UserUpload = 2
}

public enum BookVisibility
{
    Public = 1,
    Private = 2,
    Unlisted = 3
}

public enum BookProcessingStatus
{
    Draft = 1,
    Uploaded = 2,
    ExtractingText = 3,
    Extracted = 4,
    Summarizing = 5,
    SummaryReady = 6,
    GeneratingAudio = 7,
    Ready = 8,
    Failed = 9
}

public class Book
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Introduction { get; set; } = string.Empty;

    public string? CoverUrl { get; set; }

    public string Category { get; set; } = string.Empty;

    public string CoverGradient { get; set; } = string.Empty;

    public string CoverSvg { get; set; } = string.Empty;

    public BookSourceType SourceType { get; set; } = BookSourceType.Curated;

    public BookVisibility Visibility { get; set; } = BookVisibility.Public;

    public BookProcessingStatus ProcessingStatus { get; set; } = BookProcessingStatus.Ready;

    public string? OwnerUserId { get; set; }

    public ApplicationUser? OwnerUser { get; set; }

    public string Language { get; set; } = "vi";

    public decimal? Rating { get; set; }

    public int ReadingTimeMinutes { get; set; }

    public int? AudioDurationSeconds { get; set; }

    public bool IsSummaryReady { get; set; } = true;

    public bool IsAudioReady { get; set; }

    public string? FailedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; set; }

    public List<BookFile> Files { get; set; } = new();

    public List<BookContentChunk> ContentChunks { get; set; } = new();

    public List<GeneratedBookSummary> GeneratedSummaries { get; set; } = new();

    public List<BookSummarySection> SummarySections { get; set; } = new();

    public List<BookTakeaway> Takeaways { get; set; } = new();

    public List<BookAudio> Audios { get; set; } = new();

    public List<ProcessingJob> ProcessingJobs { get; set; } = new();

    public List<BookProcessingQualityReport> QualityReports { get; set; } = new();

    public List<UserBookmark> Bookmarks { get; set; } = new();

    public List<ReadingProgress> ReadingProgressEntries { get; set; } = new();

    public List<UserNote> Notes { get; set; } = new();

    public List<BookReview> Reviews { get; set; } = new();

    public List<ChatSession> ChatSessions { get; set; } = new();
}
