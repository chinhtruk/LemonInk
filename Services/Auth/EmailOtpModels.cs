namespace ZenRead.Services.Auth;

public sealed record EmailOtpSendResult(
    bool Succeeded,
    string Message,
    int ExpiresInSeconds = 0,
    int ResendAfterSeconds = 0);

public sealed record EmailOtpVerifyResult(
    bool Succeeded,
    string Message);
