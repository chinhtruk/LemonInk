namespace ZenRead.Services.Audio;

public class AudioGenerationOptions
{
    public string Provider { get; set; } = "Gemini";

    public int MaxInputCharacters { get; set; } = 6500;

    public GeminiAudioGenerationOptions Gemini { get; set; } = new();
}

public class GeminiAudioGenerationOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string EndpointBaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models";

    public string Model { get; set; } = "gemini-2.5-flash-preview-tts";

    public List<string> Models { get; set; } = new();

    public string VoiceName { get; set; } = "Kore";

    public string LanguageCode { get; set; } = "vi-VN";

    public int SampleRate { get; set; } = 24000;

    public int MinimumRequestIntervalMilliseconds { get; set; } = 22000;

    public int MaxChunkAttempts { get; set; } = 2;

    public int ChunkRetryDelayMilliseconds { get; set; } = 30000;
}
