namespace ZenRead.Services.Email;

public interface IEmailTemplateRenderer
{
    EmailContent RenderLoginOtp(string code, TimeSpan validFor);

    EmailContent RenderVerifyEmail(string displayName, string verificationUrl, TimeSpan validFor);

    EmailContent RenderPasswordResetOtp(string code, TimeSpan validFor);

    EmailContent RenderPasswordChanged(string displayName, DateTimeOffset changedAt, string securityUrl);

    EmailContent RenderEmailChanged(string displayName, string newEmail, DateTimeOffset changedAt, string securityUrl);

    EmailContent RenderNewDeviceLogin(
        string displayName,
        string deviceName,
        string approximateLocation,
        DateTimeOffset signedInAt,
        string securityUrl);

    EmailContent RenderBookUploadReceived(string displayName, string bookTitle, string trackingUrl);

    EmailContent RenderBookReady(
        string displayName,
        string bookTitle,
        string author,
        string readingUrl,
        string? audioDuration = null,
        int? chapterCount = null);

    EmailContent RenderBookProcessingFailed(string displayName, string bookTitle, string failedStep, string retryUrl);

    EmailContent RenderReviewReply(string displayName, string bookTitle, string replyExcerpt, string reviewUrl);
}
