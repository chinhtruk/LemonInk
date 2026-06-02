namespace ZenRead.Services.Summarization;

public class AiSummarizationOptions
{
    public string Provider { get; set; } = "Auto";

    public OpenAiSummarizationOptions OpenAI { get; set; } = new();

    public GeminiSummarizationOptions Gemini { get; set; } = new();

    public bool UseFallbackWhenUnavailable { get; set; } = false;

    public int MaxInputCharacters { get; set; } = 800000;

    public int MaxCharactersPerSummaryPass { get; set; } = 150000;

    public int MultiPassDelayMilliseconds { get; set; } = 13000;
}

public class OpenAiSummarizationOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4.1-mini";

    public string Endpoint { get; set; } = "https://api.openai.com/v1/responses";

    public int MaxOutputTokens { get; set; } = 8000;
}

public class GeminiSummarizationOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gemini-2.5-flash";

    public List<string> Models { get; set; } = new();

    public string ChatModel { get; set; } = "gemini-3-flash";

    public List<string> ChatModels { get; set; } = new();

    public string EndpointBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";

    public int MaxOutputTokens { get; set; } = 8000;

    public int OcrMaxOutputTokens { get; set; } = 12000;

    public int OcrMaxInlineBytes { get; set; } = 18_000_000;
}
