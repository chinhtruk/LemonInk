using ZenRead.Entities;

namespace ZenRead.Services.Audio;

public interface IAudioGenerationService
{
    bool IsConfigured { get; }

    Task<BookAudioGenerationResult> GenerateAsync(
        Book book,
        GeneratedBookSummary summary,
        IReadOnlyList<BookSummarySection> sections,
        IReadOnlyList<BookTakeaway> takeaways,
        Func<BookAudioGenerationProgress, CancellationToken, Task>? progressCallback,
        CancellationToken cancellationToken);
}
