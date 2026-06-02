using ZenRead.Entities;

namespace ZenRead.Services.Summarization;

public interface IBookSummarizationService
{
    Task<BookSummarizationResult> SummarizeAsync(
        Book book,
        IReadOnlyList<BookContentChunk> chunks,
        CancellationToken cancellationToken);
}
