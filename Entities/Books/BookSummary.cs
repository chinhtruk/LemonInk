namespace ZenRead.Entities;

public enum SummarySectionType
{
    Overview = 1,
    Chapter = 2,
    KeyIdea = 3,
    Term = 4,
    ActionableInsight = 5
}

public class GeneratedBookSummary
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string ShortSummary { get; set; } = string.Empty;

    public string LongSummary { get; set; } = string.Empty;

    public string? GeneratedBy { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    public bool EditedByAdmin { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class BookSummarySection
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public SummarySectionType SectionType { get; set; }

    public int? ChapterNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ContentHtml { get; set; } = string.Empty;

    public int ReadingTimeMinutes { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<BookContentChunk> ContentChunks { get; set; } = new();
}

public class BookTakeaway
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string Content { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
