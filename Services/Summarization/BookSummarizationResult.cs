namespace ZenRead.Services.Summarization;

public class BookSummarizationResult
{
    public string ShortSummary { get; set; } = string.Empty;

    public string LongSummary { get; set; } = string.Empty;

    public string GeneratedBy { get; set; } = "LemonInk draft summarizer";

    public List<string> KeyTakeaways { get; set; } = new();

    public List<BookChapterDraft> Chapters { get; set; } = new();

    public IReadOnlyList<SourceChapterReference> ExpectedSourceChapters { get; set; } =
        Array.Empty<SourceChapterReference>();
}

public class BookChapterDraft
{
    public int Number { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ContentHtml { get; set; } = string.Empty;

    public int ReadingTimeMinutes { get; set; }
}
