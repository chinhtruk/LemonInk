using ZenRead.Entities;

namespace ZenRead.Services.Email;

public interface IEmailNotificationService
{
    Task SendAccountVerificationAsync(string? email, string? displayName, string verificationUrl, TimeSpan validFor, CancellationToken cancellationToken = default);
    Task SendPasswordResetOtpAsync(string? email, string code, TimeSpan validFor, CancellationToken cancellationToken = default);
    Task SendPasswordChangedAsync(string? email, string? displayName, DateTimeOffset changedAt, CancellationToken cancellationToken = default);
    Task SendEmailChangedAsync(string? email, string? displayName, string newEmail, DateTimeOffset changedAt, CancellationToken cancellationToken = default);
    Task SendNewDeviceLoginAsync(string? email, string? displayName, string deviceName, string approximateLocation, DateTimeOffset signedInAt, CancellationToken cancellationToken = default);
    Task SendBookUploadReceivedAsync(ApplicationUser? owner, Book book, CancellationToken cancellationToken = default);
    Task SendBookReadyAsync(ApplicationUser? owner, Book book, string? audioDuration, int? chapterCount, CancellationToken cancellationToken = default);
    Task SendBookProcessingFailedAsync(ApplicationUser? owner, Book book, string failedStep, CancellationToken cancellationToken = default);
    Task SendReviewReplyAsync(string? email, string? displayName, string bookTitle, string replyExcerpt, int bookId, CancellationToken cancellationToken = default);
}
