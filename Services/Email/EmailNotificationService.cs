using ZenRead.Entities;

namespace ZenRead.Services.Email;

public sealed class EmailNotificationService : IEmailNotificationService
{
    private readonly IEmailSender _emailSender;
    private readonly IEmailTemplateRenderer _emailTemplates;
    private readonly string _publicBaseUrl;

    public EmailNotificationService(
        IEmailSender emailSender,
        IEmailTemplateRenderer emailTemplates,
        IConfiguration configuration)
    {
        _emailSender = emailSender;
        _emailTemplates = emailTemplates;
        _publicBaseUrl = (configuration["Email:PublicBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
    }

    public Task SendAccountVerificationAsync(string? email, string? displayName, string verificationUrl, TimeSpan validFor, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(email, _emailTemplates.RenderVerifyEmail(DisplayName(displayName), verificationUrl, validFor), cancellationToken);

    public Task SendPasswordResetOtpAsync(string? email, string code, TimeSpan validFor, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(email, _emailTemplates.RenderPasswordResetOtp(code, validFor), cancellationToken);

    public Task SendPasswordChangedAsync(string? email, string? displayName, DateTimeOffset changedAt, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(email, _emailTemplates.RenderPasswordChanged(DisplayName(displayName), changedAt, Link("/Account/Profile")), cancellationToken);

    public Task SendEmailChangedAsync(string? email, string? displayName, string newEmail, DateTimeOffset changedAt, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(email, _emailTemplates.RenderEmailChanged(DisplayName(displayName), newEmail, changedAt, Link("/Account/Profile")), cancellationToken);

    public Task SendNewDeviceLoginAsync(string? email, string? displayName, string deviceName, string approximateLocation, DateTimeOffset signedInAt, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(
            email,
            _emailTemplates.RenderNewDeviceLogin(DisplayName(displayName), deviceName, approximateLocation, signedInAt, Link("/Account/Profile")),
            cancellationToken);

    public Task SendBookUploadReceivedAsync(ApplicationUser? owner, Book book, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(
            owner?.Email,
            _emailTemplates.RenderBookUploadReceived(DisplayName(owner?.FullName), book.Title, Link("/Books/MyLibrary")),
            cancellationToken);

    public Task SendBookReadyAsync(ApplicationUser? owner, Book book, string? audioDuration, int? chapterCount, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(
            owner?.Email,
            _emailTemplates.RenderBookReady(
                DisplayName(owner?.FullName),
                book.Title,
                book.AuthorName,
                Link($"/Home/Read?id={book.Id}"),
                audioDuration,
                chapterCount),
            cancellationToken);

    public Task SendBookProcessingFailedAsync(ApplicationUser? owner, Book book, string failedStep, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(
            owner?.Email,
            _emailTemplates.RenderBookProcessingFailed(DisplayName(owner?.FullName), book.Title, failedStep, Link("/Books/MyLibrary")),
            cancellationToken);

    public Task SendReviewReplyAsync(string? email, string? displayName, string bookTitle, string replyExcerpt, int bookId, CancellationToken cancellationToken = default) =>
        SendIfDeliverableAsync(
            email,
            _emailTemplates.RenderReviewReply(DisplayName(displayName), bookTitle, replyExcerpt, Link($"/Books/Detail/{bookId}#reviews")),
            cancellationToken);

    private async Task SendIfDeliverableAsync(string? email, EmailContent content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email)
            || email.EndsWith("@external.lemonink.local", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _emailSender.SendAsync(email.Trim(), content.Subject, content.HtmlBody, content.TextBody, cancellationToken);
    }

    private static string DisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "bạn" : displayName.Trim();

    private string Link(string path) => $"{_publicBaseUrl}{path}";
}
