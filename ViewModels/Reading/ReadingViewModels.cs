namespace ZenRead.ViewModels;

public class BookSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string CoverUrl { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal Rating { get; set; }
    public bool HasRating { get; set; }
    public int ReadingTimeMinutes { get; set; }
    public bool IsAudioReady { get; set; }
    public string? AudioUrl { get; set; }
    public int? AudioDurationSeconds { get; set; }
    public string? AudioVoiceName { get; set; }
    public string Introduction { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public List<Chapter> Chapters { get; set; } = new();
    public List<string> KeyTakeaways { get; set; } = new();
}

public class Chapter
{
    public int Id { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ReadingTimeMinutes { get; set; }
}

public class ChatMessage
{
    public string Content { get; set; } = string.Empty;
    public bool IsUser { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class ChatSuggestion
{
    public string Label { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;
}
