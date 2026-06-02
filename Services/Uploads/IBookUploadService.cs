using ZenRead.ViewModels;

namespace ZenRead.Services.Uploads;

public interface IBookUploadService
{
    Task<BookUploadFormViewModel> BuildUploadFormAsync(string userId);

    Task<List<UserUploadBookItem>> GetUserUploadsAsync(string userId, int? take = null);

    Task<UserUploadBookItem?> GetProcessingStatusAsync(int bookId, string userId);

    Task<BookUploadResult> UploadAsync(BookUploadFormViewModel form, string userId);

    Task<BookUploadResult> RetryProcessingAsync(int bookId, string userId);

    Task<BookUploadResult> ReprocessFromSourceAsync(int bookId, string userId);

    Task<BookUploadResult> DeleteUploadAsync(int bookId, string userId);
}
