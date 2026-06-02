namespace ZenRead.Entities;

public class BookContentChunk
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public int? SummarySectionId { get; set; }

    public BookSummarySection? SummarySection { get; set; }

    public int ChunkIndex { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatCitation> ChatCitations { get; set; } = new();
}
