using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Email;

namespace ZenRead.Services.Auth;

public sealed class EmailOtpService : IEmailOtpService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuthenticationAuditService _audit;
    private readonly IEmailSender _emailSender;
    private readonly IEmailTemplateRenderer _emailTemplates;
    private readonly EmailOtpOptions _options;
    private readonly ILogger<EmailOtpService> _logger;

    public EmailOtpService(
        ApplicationDbContext dbContext,
        IAuthenticationAuditService audit,
        IEmailSender emailSender,
        IEmailTemplateRenderer emailTemplates,
        IOptions<EmailOtpOptions> options,
        ILogger<EmailOtpService> logger)
    {
        _dbContext = dbContext;
        _audit = audit;
        _emailSender = emailSender;
        _emailTemplates = emailTemplates;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailOtpSendResult> SendAsync(string email, CancellationToken cancellationToken = default)
    {
        var deliveryEmail = NormalizeDeliveryEmail(email);
        if (deliveryEmail is null)
        {
            return new EmailOtpSendResult(false, "Bạn nhập email hợp lệ nhé.");
        }

        var normalizedEmail = NormalizeStoredEmail(deliveryEmail);
        var now = DateTimeOffset.UtcNow;
        var existing = await FindLatestActiveChallengeAsync(normalizedEmail, cancellationToken);
        if (existing is not null && existing.ExpiresAt > now && existing.ResendAvailableAt > now)
        {
            var waitSeconds = Math.Max(1, (int)Math.Ceiling((existing.ResendAvailableAt - now).TotalSeconds));
            await _audit.RecordAsync(
                AuthenticationAuditActions.EmailOtpRequested,
                false,
                normalizedEmail,
                detail: "cooldown",
                cancellationToken: cancellationToken);
            return new EmailOtpSendResult(false, $"Bạn đợi {waitSeconds} giây rồi gửi lại mã nhé.", ResendAfterSeconds: waitSeconds);
        }

        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.TtlMinutes, 1, 30));
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(_options.ResendCooldownSeconds, 1, 600));
        var code = GenerateCode(Math.Clamp(_options.CodeLength, 4, 8));
        var salt = RandomNumberGenerator.GetBytes(16);
        var challenge = new AuthenticationOtpChallenge
        {
            NormalizedEmail = normalizedEmail,
            Purpose = AuthenticationOtpPurposes.EmailLogin,
            CodeHash = HashCode(code, salt),
            CodeSalt = salt,
            SentAt = now,
            ExpiresAt = now.Add(ttl),
            ResendAvailableAt = now.Add(cooldown),
            MaxAttempts = Math.Clamp(_options.MaxAttempts, 1, 10)
        };

        await InvalidateActiveChallengesAsync(normalizedEmail, now, cancellationToken);
        _dbContext.AuthenticationOtpChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var emailContent = _emailTemplates.RenderLoginOtp(code, ttl);
            await _emailSender.SendAsync(
                deliveryEmail,
                emailContent.Subject,
                emailContent.HtmlBody,
                emailContent.TextBody,
                cancellationToken);
        }
        catch (Exception exception)
        {
            challenge.InvalidatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            await _audit.RecordAsync(
                AuthenticationAuditActions.EmailOtpRequested,
                false,
                normalizedEmail,
                detail: "delivery-failed",
                cancellationToken: CancellationToken.None);
            _logger.LogError(exception, "Failed to send a LemonInk login OTP email to {Email}.", normalizedEmail);
            throw;
        }

        await _audit.RecordAsync(
            AuthenticationAuditActions.EmailOtpRequested,
            true,
            normalizedEmail,
            cancellationToken: cancellationToken);
        _logger.LogInformation("Created persistent email OTP challenge for {Email}, valid for {TtlMinutes} minutes.", normalizedEmail, ttl.TotalMinutes);

        return new EmailOtpSendResult(
            true,
            "Đã gửi mã OTP đến email của bạn.",
            ExpiresInSeconds: (int)ttl.TotalSeconds,
            ResendAfterSeconds: (int)cooldown.TotalSeconds);
    }

    public async Task<EmailOtpVerifyResult> VerifyAsync(
        string email,
        string code,
        CancellationToken cancellationToken = default)
    {
        var deliveryEmail = NormalizeDeliveryEmail(email);
        if (deliveryEmail is null)
        {
            return new EmailOtpVerifyResult(false, "Email không hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return new EmailOtpVerifyResult(false, "Bạn nhập mã OTP nhé.");
        }

        var normalizedEmail = NormalizeStoredEmail(deliveryEmail);
        var now = DateTimeOffset.UtcNow;
        var challenge = await FindLatestActiveChallengeAsync(normalizedEmail, cancellationToken);
        if (challenge is null)
        {
            await RecordRejectedAsync(normalizedEmail, "missing", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã OTP đã hết hạn hoặc chưa được gửi.");
        }

        if (challenge.ExpiresAt <= now)
        {
            challenge.InvalidatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecordRejectedAsync(normalizedEmail, "expired", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã OTP đã hết hạn hoặc chưa được gửi.");
        }

        if (challenge.FailedAttempts >= challenge.MaxAttempts)
        {
            challenge.InvalidatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecordRejectedAsync(normalizedEmail, "attempt-limit", cancellationToken);
            return new EmailOtpVerifyResult(false, "Bạn nhập sai quá nhiều lần. Hãy gửi lại mã mới.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                challenge.CodeHash,
                HashCode(code.Trim(), challenge.CodeSalt)))
        {
            await _dbContext.AuthenticationOtpChallenges
                .Where(candidate =>
                    candidate.Id == challenge.Id &&
                    candidate.ConsumedAt == null &&
                    candidate.InvalidatedAt == null &&
                    candidate.FailedAttempts < candidate.MaxAttempts)
                .ExecuteUpdateAsync(
                    updates => updates
                        .SetProperty(candidate => candidate.FailedAttempts, candidate => candidate.FailedAttempts + 1)
                        .SetProperty(
                            candidate => candidate.InvalidatedAt,
                            candidate => candidate.FailedAttempts + 1 >= candidate.MaxAttempts
                                ? (DateTimeOffset?)now
                                : candidate.InvalidatedAt),
                    cancellationToken);

            await RecordRejectedAsync(normalizedEmail, "wrong-code", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã OTP chưa đúng.");
        }

        var consumedCount = await _dbContext.AuthenticationOtpChallenges
            .Where(candidate =>
                candidate.Id == challenge.Id &&
                candidate.ConsumedAt == null &&
                candidate.InvalidatedAt == null &&
                candidate.ExpiresAt > now)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(candidate => candidate.ConsumedAt, now),
                cancellationToken);
        if (consumedCount != 1)
        {
            await RecordRejectedAsync(normalizedEmail, "already-used", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã OTP đã được sử dụng.");
        }

        await _audit.RecordAsync(
            AuthenticationAuditActions.EmailOtpVerified,
            true,
            normalizedEmail,
            cancellationToken: cancellationToken);
        return new EmailOtpVerifyResult(true, "Xác thực OTP thành công.");
    }

    private async Task<AuthenticationOtpChallenge?> FindLatestActiveChallengeAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        return await _dbContext.AuthenticationOtpChallenges
            .Where(challenge =>
                challenge.NormalizedEmail == normalizedEmail &&
                challenge.Purpose == AuthenticationOtpPurposes.EmailLogin &&
                challenge.ConsumedAt == null &&
                challenge.InvalidatedAt == null)
            .OrderByDescending(challenge => challenge.SentAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task InvalidateActiveChallengesAsync(
        string normalizedEmail,
        DateTimeOffset invalidatedAt,
        CancellationToken cancellationToken)
    {
        await _dbContext.AuthenticationOtpChallenges
            .Where(challenge =>
                challenge.NormalizedEmail == normalizedEmail &&
                challenge.Purpose == AuthenticationOtpPurposes.EmailLogin &&
                challenge.ConsumedAt == null &&
                challenge.InvalidatedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(challenge => challenge.InvalidatedAt, invalidatedAt),
                cancellationToken);
    }

    private Task RecordRejectedAsync(string normalizedEmail, string detail, CancellationToken cancellationToken)
    {
        return _audit.RecordAsync(
            AuthenticationAuditActions.EmailOtpRejected,
            false,
            normalizedEmail,
            detail: detail,
            cancellationToken: cancellationToken);
    }

    private static string? NormalizeDeliveryEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) || !trimmed.Contains('@')
            ? null
            : trimmed;
    }

    private static string NormalizeStoredEmail(string email)
    {
        return email.ToUpperInvariant();
    }

    private static string GenerateCode(int length)
    {
        var min = (int)Math.Pow(10, length - 1);
        var max = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(min, max).ToString();
    }

    private static byte[] HashCode(string code, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            code.Trim(),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
    }
}
