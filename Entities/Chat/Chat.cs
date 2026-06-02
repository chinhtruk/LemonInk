namespace ZenRead.Entities;

public enum ChatMessageRole
{
    System = 1,
    User = 2,
    Assistant = 3
}

public class ChatSession
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public ApplicationUser User { get; set; } = null!;

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatMessage> Messages { get; set; } = new();
}

public class ChatMessage
{
    public int Id { get; set; }

    public int ChatSessionId { get; set; }

    public ChatSession ChatSession { get; set; } = null!;

    public ChatMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatCitation> Citations { get; set; } = new();
}

public class ChatCitation
{
    public int Id { get; set; }

    public int ChatMessageId { get; set; }

    public ChatMessage ChatMessage { get; set; } = null!;

    public int BookContentChunkId { get; set; }

    public BookContentChunk BookContentChunk { get; set; } = null!;

    public string Quote { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}
