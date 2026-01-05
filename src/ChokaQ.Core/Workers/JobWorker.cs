using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

/// <summary>
/// Background service responsible for processing jobs from the queue.
/// Follows the Producer-Consumer pattern.
/// </summary>
public class JobWorker : BackgroundService
{
    private readonly ILogger<JobWorker> _logger;
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<JobWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Long-running task that listens to the channel and processes jobs sequentially.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChokaQ] Worker started.");

        // WaitToReadAsync efficiently suspends the thread until data is available.
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_queue.Reader.TryRead(out var job))
            {
                try
                {
                    // 1. Lifecycle Start: Processing
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Processing, stoppingToken);

                    // 2. Execution
                    await ProcessJobAsync(job, stoppingToken);

                    // 3. Lifecycle End: Succeeded
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Succeeded, stoppingToken);

                    _logger.LogInformation("Job done. ID: {JobId}", job.Id);
                }
                catch (Exception ex)
                {
                    // 4. Lifecycle Error: Failed
                    _logger.LogError(ex, "Job failed. ID: {JobId}", job.Id);
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed, stoppingToken);
                }
            }
        }
    }

    /// <summary>
    /// Updates persistence and sends Real-time notification in one go.
    /// </summary>
    private async Task UpdateStateAndNotifyAsync(string jobId, JobStatus status, CancellationToken ct)
    {
        // Persistence
        await _storage.UpdateJobStateAsync(jobId, status, ct);

        // Notification (Fire-and-forget style safety)
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification for Job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Resolves the appropriate handler from DI and executes it.
    /// </summary>
    private async Task ProcessJobAsync(IChokaQJob job, CancellationToken ct)
    {
        // Create a new DI scope ensures that scoped services (like DbContext) are fresh for each job.
        using var scope = _scopeFactory.CreateScope();
        var jobType = job.GetType();

        // Dynamic generic type resolution: IChokaQJobHandler<TJob>
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
        var handler = scope.ServiceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"No handler registered for {jobType.Name}");
        }

        var method = handlerType.GetMethod("HandleAsync");
        if (method != null)
        {
            await (Task)method.Invoke(handler, new object[] { job, ct })!;
        }
    }
}