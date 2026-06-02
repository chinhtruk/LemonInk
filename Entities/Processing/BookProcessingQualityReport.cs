namespace ZenRead.Entities;

public enum ProcessingQualityStage
{
    ExtractText = 1,
    SummarizeBook = 2,
    GenerateAudio = 3
}

public enum ProcessingQualityStatus
{
    Passed = 1,
    Warning = 2,
    Failed = 3
}

public class BookProcessingQualityReport
{
    public long Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int? ProcessingJobId { get; set; }

    public ProcessingJob? ProcessingJob { get; set; }

    public ProcessingQualityStage Stage { get; set; }

    public ProcessingQualityStatus Status { get; set; } = ProcessingQualityStatus.Passed;

    public int? EstimatedPageCount { get; set; }

    public int SourceChunkCount { get; set; }

    public int ExtractedWordCount { get; set; }

    public int DetectedChapterCount { get; set; }

    public int ExpectedChapterCount { get; set; }

    public int CoveredChapterCount { get; set; }

    public int MissingChapterCount { get; set; }

    public int SummarySectionCount { get; set; }

    public int SummaryWordCount { get; set; }

    public int? AudioDurationSeconds { get; set; }

    public int? ExpectedAudioDurationSeconds { get; set; }

    public int? AudioSegmentCount { get; set; }

    public int? AudioScriptCharacterCount { get; set; }

    public decimal SummaryCoveragePercent { get; set; }

    public decimal? AudioCoveragePercent { get; set; }

    public string WarningsJson { get; set; } = "[]";

    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
