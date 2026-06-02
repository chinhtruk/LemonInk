namespace ZenRead.Services.Auth;

public interface IAuthenticationAuditService
{
    Task RecordAsync(
        string action,
        bool succeeded,
        string? normalizedEmail = null,
        string? userId = null,
        string? detail = null,
        CancellationToken cancellationToken = default);
}
