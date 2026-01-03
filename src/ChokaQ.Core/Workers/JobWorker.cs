using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums; // <--- Need enums
using ChokaQ.Abstractions.Storage; // <--- Need storage
using ChokaQ.Core.Queues; // Need concrete queue class for Reader property? Better use interface if possible but here concrete is OK.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService
{
    private readonly ILogger<JobWorker> _logger;
    private readonly InMemoryQueue _queue; // Or IChokaQQueue if you expose Reader there
    private readonly IJobStorage _storage; // <--- Reporting line
    private readonly IServiceScopeFactory _scopeFactory;

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage, // <--- Inject Storage
        ILogger<JobWorker> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _storage = storage;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChokaQ] Worker started. Ready to rumble.");

        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_queue.Reader.TryRead(out var job))
            {
                try
                {
                    // 1. Report: RUNNING
                    await _storage.UpdateJobStateAsync(job.Id, JobStatus.Processing, stoppingToken);

                    // 2. Do the work
                    await ProcessJobAsync(job, stoppingToken);

                    // 3. Report: SUCCEEDED
                    await _storage.UpdateJobStateAsync(job.Id, JobStatus.Succeeded, stoppingToken);
                    _logger.LogInformation("Job done. ID: {JobId}", job.Id);
                }
                catch (Exception ex)
                {
                    // 4. Report: FAILED
                    _logger.LogError(ex, "Job failed. ID: {JobId}", job.Id);
                    await _storage.UpdateJobStateAsync(job.Id, JobStatus.Failed, stoppingToken);
                }
            }
        }
    }

    private async Task ProcessJobAsync(IChokaQJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobType = job.GetType();
        var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
        var handler = scope.ServiceProvider.GetService(handlerType);

        if (handler == null)
        {
            throw new InvalidOperationException($"No handler for {jobType.Name}");
        }

        var method = handlerType.GetMethod("HandleAsync");
        if (method != null)
        {
            await (Task)method.Invoke(handler, new object[] { job, ct })!;
        }
    }
}