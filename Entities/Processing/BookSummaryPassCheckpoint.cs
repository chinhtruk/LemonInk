namespace ZenRead.Entities;

public class BookSummaryPassCheckpoint
{
    public long Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int PassIndex { get; set; }

    public int PassCount { get; set; }

    public string SourceHash { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
