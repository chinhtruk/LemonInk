namespace ZenRead.Services.Audio;

public class BookAudioGenerationResult
{
    public string StoredAudioPath { get; set; } = string.Empty;

    public int DurationSeconds { get; set; }

    public string VoiceName { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public int SegmentCount { get; set; }

    public int ScriptCharacterCount { get; set; }
}

public class BookAudioGenerationProgress
{
    public int CompletedSegments { get; set; }

    public int TotalSegments { get; set; }

    public string Model { get; set; } = string.Empty;

    public bool LoadedFromCheckpoint { get; set; }
}
