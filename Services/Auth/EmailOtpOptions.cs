namespace ZenRead.Services.Auth;

public sealed class EmailOtpOptions
{
    public int CodeLength { get; set; } = 6;

    public int TtlMinutes { get; set; } = 5;

    public int MaxAttempts { get; set; } = 5;

    public int ResendCooldownSeconds { get; set; } = 45;
}
