namespace ZenRead.Entities;

public class UserBookmark
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ReadingProgress
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int? SummarySectionId { get; set; }

    public BookSummarySection? SummarySection { get; set; }

    public int ProgressPercent { get; set; }

    public string? LastPosition { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UserNote
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int? SummarySectionId { get; set; }

    public BookSummarySection? SummarySection { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class BookReview
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int Rating { get; set; }

    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<BookReviewReply> Replies { get; set; } = new();
}

public class BookReviewReply
{
    public int Id { get; set; }

    public int BookReviewId { get; set; }

    public BookReview Review { get; set; } = null!;

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
