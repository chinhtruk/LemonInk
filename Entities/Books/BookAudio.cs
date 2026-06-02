namespace ZenRead.Entities;

public enum AudioStatus
{
    Pending = 1,
    Generating = 2,
    Ready = 3,
    Failed = 4
}

public class BookAudio
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string AudioUrl { get; set; } = string.Empty;

    public int DurationSeconds { get; set; }

    public string? VoiceName { get; set; }

    public string? Provider { get; set; }

    public AudioStatus Status { get; set; } = AudioStatus.Pending;

    public string? FailedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
