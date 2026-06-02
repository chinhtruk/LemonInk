namespace ZenRead.Services.Auth;

public interface IEmailOtpService
{
    Task<EmailOtpSendResult> SendAsync(string email, CancellationToken cancellationToken = default);

    Task<EmailOtpVerifyResult> VerifyAsync(string email, string code, CancellationToken cancellationToken = default);
}
