namespace ZenRead.Services.Chat;

public interface IChatService
{
    Task<ChatServiceResult> AskAsync(
        string userId,
        int bookId,
        string message,
        CancellationToken cancellationToken);

    Task<ChatServiceResult> GetHistoryAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken);

    Task<ChatServiceResult> ClearAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken);
}
