using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZenRead.Data;
using ZenRead.Entities;

namespace ZenRead.Services.Ai;

public enum AiModelTask
{
    Chat = 1,
    Summarization = 2,
    Ocr = 3,
    TextToSpeech = 4
}

public interface IAiModelRouter
{
    Task<IReadOnlyList<string>> OrderModelsAsync(
        AiModelTask task,
        IEnumerable<string> configuredModels,
        CancellationToken cancellationToken = default);

    Task RegisterModelsAsync(
        AiModelTask task,
        IEnumerable<string> configuredModels,
        CancellationToken cancellationToken = default);

    Task ReportSuccessAsync(
        AiModelTask task,
        string model,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task ReportFailureAsync(
        AiModelTask task,
        string model,
        Exception exception,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiModelHealthSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class AiModelHealthSnapshot
{
    public AiModelTask Task { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Status { get; init; } = "healthy";
    public long SuccessCount { get; init; }
    public long FailureCount { get; init; }
    public long RateLimitFailureCount { get; init; }
    public long QuotaFailureCount { get; init; }
    public long TimeoutFailureCount { get; init; }
    public long ProviderUnavailableFailureCount { get; init; }
    public long AverageDurationMilliseconds { get; init; }
    public decimal SuccessRatePercent { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public DateTime? LastFailureAt { get; init; }
    public DateTime? CooldownUntil { get; init; }
    public string? LastFailureKind { get; init; }
    public string? LastError { get; init; }
}

public sealed partial class AiModelRouter : IAiModelRouter
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AiModelRouter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<string>> OrderModelsAsync(
        AiModelTask task,
        IEnumerable<string> configuredModels,
        CancellationToken cancellationToken = default)
    {
        var models = NormalizeModels(configuredModels).ToList();
        await RegisterModelsAsync(task, models, cancellationToken);

        if (models.Count == 0)
        {
            return Array.Empty<string>();
        }

        var now = DateTime.UtcNow;
        var states = (await GetSnapshotAsync(cancellationToken))
            .Where(item => item.Task == task)
            .ToDictionary(item => item.Model, StringComparer.OrdinalIgnoreCase);

        var rankedModels = models
            .Select(model => new
            {
                Model = model,
                State = states.GetValueOrDefault(model)
            })
            .OrderBy(item => item.State?.CooldownUntil.HasValue == true &&
                             item.State.CooldownUntil > now)
            .ThenByDescending(item => item.State?.LastSuccessAt.HasValue == true)
            .ThenBy(item => item.State?.FailureCount ?? 0)
            .ThenBy(item => item.State?.CooldownUntil ?? DateTime.MinValue)
            .ToList();
        var readyModels = rankedModels
            .Where(item => item.State?.CooldownUntil is not { } cooldownUntil || cooldownUntil <= now)
            .ToList();

        return (readyModels.Count > 0 ? readyModels : rankedModels)
            .Select(item => item.Model)
            .ToList();
    }

    public async Task RegisterModelsAsync(
        AiModelTask task,
        IEnumerable<string> configuredModels,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        foreach (var model in NormalizeModels(configuredModels))
        {
            await ResolveOrCreateMonitorAsync(dbContext, task, model, cancellationToken);
        }
    }

    public Task ReportSuccessAsync(
        AiModelTask task,
        string model,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        return RecordOperationAsync(task, model, succeeded: true, null, duration, cancellationToken);
    }

    public Task ReportFailureAsync(
        AiModelTask task,
        string model,
        Exception exception,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        return RecordOperationAsync(task, model, succeeded: false, exception, duration, cancellationToken);
    }

    public async Task<IReadOnlyList<AiModelHealthSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var monitors = await dbContext.AiModelMonitors
            .AsNoTracking()
            .OrderBy(item => item.Task)
            .ThenBy(item => item.Model)
            .ToListAsync(cancellationToken);

        var snapshots = new List<AiModelHealthSnapshot>(monitors.Count);
        var now = DateTime.UtcNow;
        foreach (var monitor in monitors)
        {
            var eventsQuery = dbContext.AiModelOperationEvents
                .AsNoTracking()
                .Where(item => item.AiModelMonitorId == monitor.Id);
            var aggregate = await eventsQuery
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    SuccessCount = group.LongCount(item => item.Succeeded),
                    FailureCount = group.LongCount(item => !item.Succeeded),
                    RateLimitCount = group.LongCount(item => item.FailureKind == "rate-limit"),
                    QuotaCount = group.LongCount(item => item.FailureKind == "quota"),
                    TimeoutCount = group.LongCount(item => item.FailureKind == "timeout"),
                    UnavailableCount = group.LongCount(item => item.FailureKind == "unavailable"),
                    TotalDuration = group.Sum(item => (long)item.DurationMilliseconds),
                    LastSuccessAt = group.Where(item => item.Succeeded).Max(item => (DateTime?)item.OccurredAt),
                    LastFailureAt = group.Where(item => !item.Succeeded).Max(item => (DateTime?)item.OccurredAt)
                })
                .SingleOrDefaultAsync(cancellationToken);
            var latestEvent = await eventsQuery
                .OrderByDescending(item => item.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
            var latestFailure = await eventsQuery
                .Where(item => !item.Succeeded)
                .OrderByDescending(item => item.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);
            var successCount = aggregate?.SuccessCount ?? 0;
            var failureCount = aggregate?.FailureCount ?? 0;
            var operationCount = successCount + failureCount;
            var activeCooldown = latestEvent?.Succeeded == false &&
                                 latestEvent.CooldownUntil.HasValue &&
                                 latestEvent.CooldownUntil > now
                ? latestEvent.CooldownUntil
                : null;

            snapshots.Add(new AiModelHealthSnapshot
            {
                Task = Enum.TryParse<AiModelTask>(monitor.Task, out var task) ? task : default,
                Model = monitor.Model,
                Status = ResolveStatus(successCount, failureCount, activeCooldown, now),
                SuccessCount = successCount,
                FailureCount = failureCount,
                RateLimitFailureCount = aggregate?.RateLimitCount ?? 0,
                QuotaFailureCount = aggregate?.QuotaCount ?? 0,
                TimeoutFailureCount = aggregate?.TimeoutCount ?? 0,
                ProviderUnavailableFailureCount = aggregate?.UnavailableCount ?? 0,
                AverageDurationMilliseconds = operationCount == 0
                    ? 0
                    : (aggregate?.TotalDuration ?? 0) / operationCount,
                SuccessRatePercent = operationCount == 0
                    ? 0
                    : Math.Round((decimal)successCount * 100m / operationCount, 1),
                LastSuccessAt = aggregate?.LastSuccessAt,
                LastFailureAt = aggregate?.LastFailureAt,
                CooldownUntil = activeCooldown,
                LastFailureKind = latestFailure?.FailureKind,
                LastError = latestFailure?.ErrorMessage
            });
        }

        return snapshots
            .OrderBy(item => item.Task)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task RecordOperationAsync(
        AiModelTask task,
        string model,
        bool succeeded,
        Exception? exception,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var monitor = await ResolveOrCreateMonitorAsync(dbContext, task, model.Trim(), cancellationToken);
        var failureKind = exception is null ? null : ClassifyFailure(exception);
        DateTime? cooldownUntil = exception is null ? null : DateTime.UtcNow + ResolveCooldown(exception);

        dbContext.AiModelOperationEvents.Add(new AiModelOperationEvent
        {
            AiModelMonitorId = monitor.Id,
            Succeeded = succeeded,
            DurationMilliseconds = (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue),
            FailureKind = failureKind,
            IsRetryable = failureKind is "rate-limit" or "quota" or "timeout" or "unavailable",
            ErrorMessage = exception is null ? null : TrimError(exception.Message),
            CooldownUntil = cooldownUntil,
            OccurredAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<AiModelMonitor> ResolveOrCreateMonitorAsync(
        ApplicationDbContext dbContext,
        AiModelTask task,
        string model,
        CancellationToken cancellationToken)
    {
        var taskName = task.ToString();
        var existing = await dbContext.AiModelMonitors
            .FirstOrDefaultAsync(item => item.Task == taskName && item.Model == model, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var monitor = new AiModelMonitor { Task = taskName, Model = model };
        dbContext.AiModelMonitors.Add(monitor);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return monitor;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return await dbContext.AiModelMonitors
                .SingleAsync(item => item.Task == taskName && item.Model == model, cancellationToken);
        }
    }

    private static IEnumerable<string> NormalizeModels(IEnumerable<string> configuredModels)
    {
        return configuredModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveStatus(
        long successCount,
        long failureCount,
        DateTime? cooldownUntil,
        DateTime now)
    {
        if (cooldownUntil.HasValue && cooldownUntil > now)
        {
            return "cooldown";
        }

        if (failureCount > 0 && successCount == 0)
        {
            return "warning";
        }

        return "healthy";
    }

    private static string ClassifyFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
        {
            return "quota";
        }

        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return "rate-limit";
        }

        if (exception is TaskCanceledException ||
            exception is TimeoutException ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        if (message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "unavailable";
        }

        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return "authentication";
        }

        if (exception is JsonException ||
            message.Contains("JSON", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("response", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid-response";
        }

        return "provider-error";
    }

    private static TimeSpan ResolveCooldown(Exception exception)
    {
        var message = exception.Message;
        var retryAfter = ExtractRetryAfter(message);
        if (retryAfter.HasValue)
        {
            return retryAfter.Value;
        }

        var failureKind = ClassifyFailure(exception);
        return failureKind switch
        {
            "rate-limit" or "quota" => TimeSpan.FromMinutes(3),
            "timeout" or "unavailable" => TimeSpan.FromSeconds(90),
            _ when message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("504", StringComparison.OrdinalIgnoreCase) => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromSeconds(20)
        };
    }

    private static TimeSpan? ExtractRetryAfter(string message)
    {
        var match = RetryAfterRegex().Match(message);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups["seconds"].Value, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds + 5, 15, 6 * 60 * 60))
            : null;
    }

    private static string TrimError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var clean = WhitespaceRegex().Replace(message, " ").Trim();
        return clean.Length <= 500 ? clean : clean[..497] + "...";
    }

    [GeneratedRegex(@"retry in\s+(?<seconds>\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryAfterRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
