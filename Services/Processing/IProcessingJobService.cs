namespace ZenRead.Services.Processing;

public interface IProcessingJobService
{
    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken);
}
