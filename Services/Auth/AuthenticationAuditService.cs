using ZenRead.Data;
using ZenRead.Entities;

namespace ZenRead.Services.Auth;

public sealed class AuthenticationAuditService : IAuthenticationAuditService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthenticationAuditService(
        ApplicationDbContext dbContext,
        IHttpContextAccessor httpContextAccessor)
    {
        _dbContext = dbContext;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task RecordAsync(
        string action,
        bool succeeded,
        string? normalizedEmail = null,
        string? userId = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        var context = _httpContextAccessor.HttpContext;
        var userAgent = context?.Request.Headers.UserAgent.ToString();

        _dbContext.AuthenticationAuditEvents.Add(new AuthenticationAuditEvent
        {
            Action = action,
            Succeeded = succeeded,
            NormalizedEmail = string.IsNullOrWhiteSpace(normalizedEmail) ? null : normalizedEmail,
            UserId = string.IsNullOrWhiteSpace(userId) ? null : userId,
            Detail = Truncate(detail, 240),
            IpAddress = Truncate(context?.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Truncate(userAgent, 500),
            CreatedAt = DateTimeOffset.UtcNow
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];
    }
}
