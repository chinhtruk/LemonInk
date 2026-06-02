namespace ZenRead.ViewModels;

public class LibraryViewModel
{
    public string Greeting { get; set; } = string.Empty;
    public List<BookCard> FeaturedBooks { get; set; } = new();
    public List<BookCard> RecentBooks { get; set; } = new();
    public List<HomeContinueReadingItem> ContinueReading { get; set; } = new();
    public List<string> Categories { get; set; } = new();
}

public class HomeContinueReadingItem
{
    public BookCard Book { get; set; } = new();
    public int Progress { get; set; }
    public string ProgressLabel { get; set; } = string.Empty;
    public string LastOpened { get; set; } = string.Empty;
}

public class BookCard
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Rating { get; set; }
    public bool HasRating { get; set; }
    public int ReadingTimeMinutes { get; set; }
    public string CoverUrl { get; set; } = string.Empty;
    public string CoverGradient { get; set; } = string.Empty; // CSS gradient for mock cover
    public string CoverSvg { get; set; } = string.Empty; // Inline SVG for cover decoration
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsAudioReady { get; set; }
    public bool CanRead { get; set; } = true;
}

public class BookSearchQuery
{
    public string? Query { get; set; }
    public string? Source { get; set; }
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? Sort { get; set; }
}

public class BookSearchResult
{
    public List<BookCard> Books { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public int TotalCount { get; set; }
    public int FilteredCount { get; set; }
}

public class BookDetailViewModel
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string CoverGradient { get; set; } = string.Empty;
    public string Introduction { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Rating { get; set; }
    public bool HasRating { get; set; }
    public int ReviewCount { get; set; }
    public int ReadingTimeMinutes { get; set; }
    public int? AudioDurationSeconds { get; set; }
    public int ChaptersCount { get; set; }
    public bool IsAudioReady { get; set; }
    public bool CanRead { get; set; }
    public bool CanReview { get; set; }
    public BookReviewItem? UserReview { get; set; }
    public List<string> KeyTakeaways { get; set; } = new();
    public List<BookReviewItem> Reviews { get; set; } = new();
}

public class BookReviewItem
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool CanManage { get; set; }
}
