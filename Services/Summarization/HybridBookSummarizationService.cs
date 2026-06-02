using Microsoft.Extensions.Options;
using ZenRead.Entities;

namespace ZenRead.Services.Summarization;

public class HybridBookSummarizationService : IBookSummarizationService
{
    private readonly OpenAiBookSummarizationService _openAiSummarizationService;
    private readonly GeminiBookSummarizationService _geminiSummarizationService;
    private readonly DraftBookSummarizationService _draftSummarizationService;
    private readonly AiSummarizationOptions _options;
    private readonly ILogger<HybridBookSummarizationService> _logger;

    public HybridBookSummarizationService(
        OpenAiBookSummarizationService openAiSummarizationService,
        GeminiBookSummarizationService geminiSummarizationService,
        DraftBookSummarizationService draftSummarizationService,
        IOptions<AiSummarizationOptions> options,
        ILogger<HybridBookSummarizationService> logger)
    {
        _openAiSummarizationService = openAiSummarizationService;
        _geminiSummarizationService = geminiSummarizationService;
        _draftSummarizationService = draftSummarizationService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<BookSummarizationResult> SummarizeAsync(
        Book book,
        IReadOnlyList<BookContentChunk> chunks,
        CancellationToken cancellationToken)
    {
        var provider = _options.Provider.Trim();
        var useGemini = provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Google", StringComparison.OrdinalIgnoreCase) ||
            (provider.Equals("Auto", StringComparison.OrdinalIgnoreCase) && _geminiSummarizationService.IsConfigured);
        var useOpenAi = provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            (provider.Equals("Auto", StringComparison.OrdinalIgnoreCase) && !_geminiSummarizationService.IsConfigured && _openAiSummarizationService.IsConfigured);

        if (!useGemini && !useOpenAi)
        {
            return await _draftSummarizationService.SummarizeAsync(book, chunks, cancellationToken);
        }

        try
        {
            if (useGemini)
            {
                return await _geminiSummarizationService.SummarizeAsync(book, chunks, cancellationToken);
            }

            return await _openAiSummarizationService.SummarizeAsync(book, chunks, cancellationToken);
        }
        catch (Exception exception) when (_options.UseFallbackWhenUnavailable)
        {
            _logger.LogWarning(exception, "AI summarization failed for book {BookId}; using draft fallback.", book.Id);
            var fallback = await _draftSummarizationService.SummarizeAsync(book, chunks, cancellationToken);
            fallback.GeneratedBy = "LemonInk draft summarizer fallback";
            return fallback;
        }
    }
}
