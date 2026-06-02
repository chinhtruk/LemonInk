namespace ZenRead.Entities;

public static class AuthenticationOtpPurposes
{
    public const string EmailLogin = "email-login";

    public const string PasswordReset = "password-reset";
}

public static class AuthenticationAuditActions
{
    public const string PasswordLogin = "login.password";

    public const string ExternalLogin = "login.external";

    public const string NewDeviceLogin = "login.new-device";

    public const string SessionsRevoked = "sessions.revoked";

    public const string EmailOtpRequested = "login.email-otp.requested";

    public const string EmailOtpVerified = "login.email-otp.verified";

    public const string EmailOtpRejected = "login.email-otp.rejected";

    public const string PasswordResetOtpRequested = "password-reset.otp.requested";

    public const string PasswordResetOtpVerified = "password-reset.otp.verified";

    public const string PasswordResetOtpRejected = "password-reset.otp.rejected";

    public const string PasswordResetCompleted = "password-reset.completed";

    public const string PasswordChanged = "password.changed";

    public const string EmailOtpRegistrationCompleted = "registration.email-otp.completed";
}

public class AuthenticationOtpChallenge
{
    public long Id { get; set; }

    public string NormalizedEmail { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public byte[] CodeHash { get; set; } = Array.Empty<byte>();

    public byte[] CodeSalt { get; set; } = Array.Empty<byte>();

    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset ResendAvailableAt { get; set; }

    public int FailedAttempts { get; set; }

    public int MaxAttempts { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? InvalidatedAt { get; set; }
}

public class AuthenticationAuditEvent
{
    public long Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string? UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public string? NormalizedEmail { get; set; }

    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
