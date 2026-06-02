namespace ZenRead.Services.Processing;

public class ProcessingJobWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcessingJobWorker> _logger;

    public ProcessingJobWorker(IServiceScopeFactory scopeFactory, ILogger<ProcessingJobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IProcessingJobService>();
                var processedJob = await processor.ProcessNextQueuedJobAsync(stoppingToken);

                if (!processedJob)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Processing worker failed while polling jobs.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }
}
