using ZenRead.ViewModels;

namespace ZenRead.Services.UserLibrary;

public class BookmarkToggleResult
{
    public bool Succeeded { get; set; }

    public bool NotFound { get; set; }

    public bool Forbidden { get; set; }

    public bool IsBookmarked { get; set; }

    public string Message { get; set; } = string.Empty;
}

public class BookmarkStatusResult
{
    public bool NotFound { get; set; }

    public bool Forbidden { get; set; }

    public bool IsBookmarked { get; set; }
}

public interface IBookmarkService
{
    Task<BookmarkPageViewModel> GetBookmarkPageAsync(string userId, CancellationToken cancellationToken = default);

    Task<BookmarkToggleResult> ToggleAsync(string userId, int bookId, CancellationToken cancellationToken = default);

    Task<BookmarkStatusResult> GetStatusAsync(string userId, int bookId, CancellationToken cancellationToken = default);
}
