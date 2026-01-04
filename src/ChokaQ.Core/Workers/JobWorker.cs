using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService
{
    private readonly ILogger<JobWorker> _logger;
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier; // Service for real-time notifications
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        IChokaQNotifier notifier, // Injecting the notifier
        ILogger<JobWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChokaQ] Worker started. Ready to rumble.");

        // Wait until data is available in the channel
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            // Try to read the job from the channel
            while (_queue.Reader.TryRead(out var job))
            {
                try
                {
                    // 1. Report state: PROCESSING
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Processing, stoppingToken);

                    // 2. Execute the actual job logic
                    await ProcessJobAsync(job, stoppingToken);

                    // 3. Report state: SUCCEEDED
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Succeeded, stoppingToken);

                    _logger.LogInformation("Job done. ID: {JobId}", job.Id);
                }
                catch (Exception ex)
                {
                    // 4. Handle failure and report state: FAILED
                    _logger.LogError(ex, "Job failed. ID: {JobId}", job.Id);
                    await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed, stoppingToken);
                }
            }
        }
    }

    /// <summary>
    /// Helper method to update the job status in storage and send a real-time notification.
    /// </summary>
    private async Task UpdateStateAndNotifyAsync(string jobId, JobStatus status, CancellationToken ct)
    {
        // 1. Update the persistence layer (Database/Memory)
        await _storage.UpdateJobStateAsync(jobId, status, ct);

        // 2. Send notification via SignalR (wrapped in try-catch to ensure worker stability)
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, status);
        }
        catch (Exception ex)
        {
            // Log the error but do not stop the worker if notification fails
            _logger.LogError(ex, "Failed to send SignalR notification for Job {JobId}", jobId);
        }
    }

    private async Task ProcessJobAsync(IChokaQJob job, CancellationToken ct)
    {
        // Create a DI scope for the handler to support scoped services (like EF Core DbContext)
        using var scope = _scopeFactory.CreateScope();
        var jobType = job.GetType();

        // Resolve the specific handler for this job type
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
        var handler = scope.ServiceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"No handler registered for {jobType.Name}");
        }

        // Invoke the HandleAsync method via Reflection
        var method = handlerType.GetMethod("HandleAsync");
        if (method != null)
        {
            await (Task)method.Invoke(handler, new object[] { job, ct })!;
        }
    }
}