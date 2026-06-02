using ZenRead.ViewModels;

namespace ZenRead.Services.Books;

public interface IBookService
{
    Task<LibraryViewModel> GetHomeLibraryAsync(string? userId = null);

    Task<List<BookCard>> GetPublicLibraryAsync();

    Task<BookSearchResult> SearchBooksAsync(BookSearchQuery query, string? userId = null);

    Task<BookSummary?> GetBookSummaryAsync(int id, string? userId = null);

    Task<BookDetailViewModel?> GetBookDetailAsync(int id, string? userId = null);

    Task<bool> SaveBookReviewAsync(int bookId, string userId, int rating, string? comment);

    Task<bool> DeleteBookReviewAsync(int bookId, string userId);

    Task<bool> ReplyToBookReviewAsync(int bookId, int reviewId, string userId, string? content);
}
