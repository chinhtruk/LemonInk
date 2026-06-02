namespace ZenRead.Services.Processing;

public class OcrProviderTemporaryException : InvalidOperationException
{
    public OcrProviderTemporaryException(
        int statusCode,
        string? reasonPhrase,
        string message,
        TimeSpan retryAfter,
        string responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        RetryAfter = retryAfter;
        ResponseBody = responseBody;
    }

    public int StatusCode { get; }

    public string? ReasonPhrase { get; }

    public TimeSpan RetryAfter { get; }

    public string ResponseBody { get; }
}
