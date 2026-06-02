namespace ZenRead.Entities;

public class AiModelMonitor
{
    public long Id { get; set; }
    public string Task { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AiModelOperationEvent> Events { get; set; } = new List<AiModelOperationEvent>();
}

public class AiModelOperationEvent
{
    public long Id { get; set; }
    public long AiModelMonitorId { get; set; }
    public AiModelMonitor Monitor { get; set; } = null!;
    public bool Succeeded { get; set; }
    public int DurationMilliseconds { get; set; }
    public string? FailureKind { get; set; }
    public bool IsRetryable { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? CooldownUntil { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
