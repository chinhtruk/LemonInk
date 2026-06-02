namespace ZenRead.Services.UserLibrary;

public class ReadingProgressUpdateRequest
{
    public int BookId { get; set; }

    public int? SummarySectionId { get; set; }

    public int ProgressPercent { get; set; }

    public string? LastPosition { get; set; }
}

public class ReadingProgressResult
{
    public bool Succeeded { get; set; }

    public bool NotFound { get; set; }

    public bool Forbidden { get; set; }

    public int ProgressPercent { get; set; }

    public int? SummarySectionId { get; set; }

    public string? LastPosition { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

public interface IReadingProgressService
{
    Task<ReadingProgressResult> UpdateAsync(
        string userId,
        ReadingProgressUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<ReadingProgressResult> GetAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken = default);
}
