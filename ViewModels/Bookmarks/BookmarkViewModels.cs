namespace ZenRead.ViewModels;

public class BookmarkPageViewModel
{
    public int TotalSavedBooks { get; set; }
    public int ContinueReadingCount { get; set; }
    public int AudioReadyCount { get; set; }
    public int NotesCount { get; set; }
    public List<string> Filters { get; set; } = new();
    public List<BookmarkItem> ContinueReading { get; set; } = new();
    public List<BookmarkItem> SavedBooks { get; set; } = new();
}

public class BookmarkItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Rating { get; set; }
    public int ReadingTimeMinutes { get; set; }
    public string CoverGradient { get; set; } = string.Empty;
    public string CoverSvg { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public string ProgressLabel { get; set; } = string.Empty;
    public string LastOpenedLabel { get; set; } = string.Empty;
    public bool HasAudio { get; set; }
    public bool HasNotes { get; set; }
    public string NoteSnippet { get; set; } = string.Empty;
    public bool IsContinueReading { get; set; }
    public int OrderIndex { get; set; }
}
