using ZenRead.Entities;

namespace ZenRead.Services.Processing;

public interface ITextExtractionService
{
    Task<string> ExtractAsync(BookFile file, CancellationToken cancellationToken);
}
