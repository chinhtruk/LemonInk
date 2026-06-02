namespace ZenRead.Services.Chat;

public class ChatAskRequest
{
    public int BookId { get; set; }

    public string Message { get; set; } = string.Empty;
}

public class ChatServiceResult
{
    public bool Succeeded { get; set; }

    public bool NotFound { get; set; }

    public bool Forbidden { get; set; }

    public string? ErrorMessage { get; set; }

    public ChatMessageDto? Message { get; set; }

    public List<ChatMessageDto> Messages { get; set; } = new();

    public static ChatServiceResult Success(ChatMessageDto message)
    {
        return new ChatServiceResult
        {
            Succeeded = true,
            Message = message
        };
    }

    public static ChatServiceResult Fail(string message)
    {
        return new ChatServiceResult
        {
            ErrorMessage = message
        };
    }
}

public class ChatMessageDto
{
    public int Id { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public List<ChatCitationDto> Citations { get; set; } = new();
}

public class ChatCitationDto
{
    public int ChunkIndex { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Quote { get; set; } = string.Empty;
}
