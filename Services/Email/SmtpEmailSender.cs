using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Extensions.Options;

namespace ZenRead.Services.Email;

public sealed class SmtpEmailSender : IEmailSender
{
    private const string BrandLogoContentId = "lemonink-logo";

    private readonly SmtpEmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly IWebHostEnvironment _environment;

    public SmtpEmailSender(
        IOptions<SmtpEmailOptions> options,
        ILogger<SmtpEmailSender> logger,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _logger = logger;
        _environment = environment;
    }

    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string? textBody = null,
        CancellationToken cancellationToken = default)
    {
        if (_options.LogEmailsInsteadOfSending)
        {
            _logger.LogInformation("Email to {ToEmail}: {Subject}\n{TextBody}", toEmail, subject, textBody ?? htmlBody);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Username) || string.IsNullOrWhiteSpace(_options.Password))
        {
            throw new InvalidOperationException("SMTP username/password chưa được cấu hình.");
        }

        var fromEmail = string.IsNullOrWhiteSpace(_options.FromEmail) ? _options.Username : _options.FromEmail;

        using var message = new MailMessage
        {
            From = new MailAddress(fromEmail, _options.FromName),
            Subject = subject,
            Body = textBody ?? string.Empty,
            IsBodyHtml = false
        };

        message.To.Add(new MailAddress(toEmail));

        if (!string.IsNullOrWhiteSpace(textBody))
        {
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));
        }

        var htmlView = AlternateView.CreateAlternateViewFromString(htmlBody, null, MediaTypeNames.Text.Html);
        AddBrandLogoIfUsed(htmlBody, htmlView);
        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        using var registration = cancellationToken.Register(client.SendAsyncCancel);
        await client.SendMailAsync(message, cancellationToken);
    }

    private void AddBrandLogoIfUsed(string htmlBody, AlternateView htmlView)
    {
        if (!htmlBody.Contains($"cid:{BrandLogoContentId}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var logoPath = Path.Combine(_environment.WebRootPath, "images", "LemonInk.png");
        if (!File.Exists(logoPath))
        {
            _logger.LogWarning("Email template requested the LemonInk logo, but {LogoPath} could not be found.", logoPath);
            return;
        }

        var logo = new LinkedResource(logoPath, MediaTypeNames.Image.Png)
        {
            ContentId = BrandLogoContentId,
            TransferEncoding = TransferEncoding.Base64
        };

        htmlView.LinkedResources.Add(logo);
    }
}
