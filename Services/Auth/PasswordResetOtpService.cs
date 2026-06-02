using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Email;

namespace ZenRead.Services.Auth;

public sealed class PasswordResetOtpService : IPasswordResetOtpService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IAuthenticationAuditService _audit;
    private readonly IEmailNotificationService _emailNotifications;
    private readonly EmailOtpOptions _options;
    private readonly ILogger<PasswordResetOtpService> _logger;

    public PasswordResetOtpService(
        ApplicationDbContext dbContext,
        IAuthenticationAuditService audit,
        IEmailNotificationService emailNotifications,
        IOptions<EmailOtpOptions> options,
        ILogger<PasswordResetOtpService> logger)
    {
        _dbContext = dbContext;
        _audit = audit;
        _emailNotifications = emailNotifications;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailOtpSendResult> SendAsync(string email, CancellationToken cancellationToken = default)
    {
        var deliveryEmail = email.Trim();
        var normalizedEmail = NormalizeStoredEmail(deliveryEmail);
        var now = DateTimeOffset.UtcNow;
        var existing = await FindLatestActiveChallengeAsync(normalizedEmail, cancellationToken);
        if (existing is not null && existing.ExpiresAt > now && existing.ResendAvailableAt > now)
        {
            var waitSeconds = Math.Max(1, (int)Math.Ceiling((existing.ResendAvailableAt - now).TotalSeconds));
            await _audit.RecordAsync(
                AuthenticationAuditActions.PasswordResetOtpRequested,
                false,
                normalizedEmail,
                detail: "cooldown",
                cancellationToken: cancellationToken);
            return new EmailOtpSendResult(false, $"Vui lòng chờ {waitSeconds} giây trước khi gửi lại mã.", 0, waitSeconds);
        }

        var ttl = TimeSpan.FromMinutes(Math.Clamp(_options.TtlMinutes, 1, 30));
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(_options.ResendCooldownSeconds, 1, 600));
        var code = GenerateCode();
        var salt = RandomNumberGenerator.GetBytes(16);
        var challenge = new AuthenticationOtpChallenge
        {
            NormalizedEmail = normalizedEmail,
            Purpose = AuthenticationOtpPurposes.PasswordReset,
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
            await _emailNotifications.SendPasswordResetOtpAsync(deliveryEmail, code, ttl, cancellationToken);
        }
        catch (Exception exception)
        {
            challenge.InvalidatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            await _audit.RecordAsync(
                AuthenticationAuditActions.PasswordResetOtpRequested,
                false,
                normalizedEmail,
                detail: "delivery-failed",
                cancellationToken: CancellationToken.None);
            _logger.LogError(exception, "Failed to send a LemonInk password reset OTP email to {Email}.", normalizedEmail);
            throw;
        }

        await _audit.RecordAsync(
            AuthenticationAuditActions.PasswordResetOtpRequested,
            true,
            normalizedEmail,
            cancellationToken: cancellationToken);
        return new EmailOtpSendResult(
            true,
            "Đã gửi mã đặt lại mật khẩu tới email của bạn.",
            (int)ttl.TotalSeconds,
            (int)cooldown.TotalSeconds);
    }

    public async Task<EmailOtpVerifyResult> VerifyAsync(
        string email,
        string code,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeStoredEmail(email.Trim());
        var now = DateTimeOffset.UtcNow;
        var challenge = await FindLatestActiveChallengeAsync(normalizedEmail, cancellationToken);
        if (challenge is null)
        {
            await RecordRejectedAsync(normalizedEmail, "missing", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã xác minh không còn hiệu lực.");
        }

        if (challenge.ExpiresAt <= now)
        {
            challenge.InvalidatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecordRejectedAsync(normalizedEmail, "expired", cancellationToken);
            return new EmailOtpVerifyResult(false, "Mã xác minh đã hết hạn.");
        }

        if (challenge.FailedAttempts >= challenge.MaxAttempts)
        {
            challenge.InvalidatedAt = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await RecordRejectedAsync(normalizedEmail, "attempt-limit", cancellationToken);
            return new EmailOtpVerifyResult(false, "Bạn đã nhập sai quá số lần cho phép.");
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
            return new EmailOtpVerifyResult(false, "Mã xác minh không đúng.");
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
            return new EmailOtpVerifyResult(false, "Mã xác minh không còn hiệu lực.");
        }

        await _audit.RecordAsync(
            AuthenticationAuditActions.PasswordResetOtpVerified,
            true,
            normalizedEmail,
            cancellationToken: cancellationToken);
        return new EmailOtpVerifyResult(true, "Mã xác minh hợp lệ.");
    }

    private async Task<AuthenticationOtpChallenge?> FindLatestActiveChallengeAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        return await _dbContext.AuthenticationOtpChallenges
            .Where(challenge =>
                challenge.NormalizedEmail == normalizedEmail &&
                challenge.Purpose == AuthenticationOtpPurposes.PasswordReset &&
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
                challenge.Purpose == AuthenticationOtpPurposes.PasswordReset &&
                challenge.ConsumedAt == null &&
                challenge.InvalidatedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(challenge => challenge.InvalidatedAt, invalidatedAt),
                cancellationToken);
    }

    private Task RecordRejectedAsync(string normalizedEmail, string detail, CancellationToken cancellationToken)
    {
        return _audit.RecordAsync(
            AuthenticationAuditActions.PasswordResetOtpRejected,
            false,
            normalizedEmail,
            detail: detail,
            cancellationToken: cancellationToken);
    }

    private string GenerateCode()
    {
        var length = Math.Clamp(_options.CodeLength, 4, 8);
        var maxValue = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(maxValue).ToString($"D{length}");
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

    private static string NormalizeStoredEmail(string email)
    {
        return email.ToUpperInvariant();
    }
}
