using ZenRead.Entities;

namespace ZenRead.Services.Covers;

public interface IBookCoverService
{
    Task<string> EnsureCoverAsync(Book book, CancellationToken cancellationToken);

    string BuildGradient(Book book);
}
