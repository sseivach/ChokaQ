using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Workers;

/// <summary>
/// Worker Manager. Manages a pool of tasks that consume jobs from the queue.
/// </summary>
public class JobWorker : BackgroundService, IWorkerManager
{
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<JobWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // List of active workers: (The running Task, The cancellation source to stop it)
    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();

    // Lock to ensure thread safety when scaling workers
    private readonly object _lock = new();

    public int ActiveWorkers { get; private set; } = 0;

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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start with 1 worker by default
        UpdateWorkerCount(1);

        // The BackgroundService itself just waits for the app to shutdown
        return Task.CompletedTask;
    }

    public void UpdateWorkerCount(int targetCount)
    {
        if (targetCount < 0) targetCount = 0;
        if (targetCount > 10) targetCount = 10; // Hard limit

        lock (_lock)
        {
            int current = _workers.Count;
            _logger.LogInformation("Scaling workers: {Current} -> {Target}", current, targetCount);

            if (targetCount > current)
            {
                // SCALE UP: Add new workers
                int toAdd = targetCount - current;
                for (int i = 0; i < toAdd; i++)
                {
                    var cts = new CancellationTokenSource();
                    // Start a separate thread (Task) for the worker loop
                    var task = Task.Run(() => WorkerLoopAsync(cts.Token), cts.Token);
                    _workers.Add((task, cts));
                }
            }
            else if (targetCount < current)
            {
                // SCALE DOWN: Remove excess workers
                int toRemove = current - targetCount;
                for (int i = 0; i < toRemove; i++)
                {
                    // Pick the last worker
                    var worker = _workers.Last();

                    // Signal it to stop (Cancel)
                    worker.Cts.Cancel();

                    // Remove from the management list immediately
                    // (The task might still be finishing its current job, but it will stop afterwards)
                    _workers.RemoveAt(_workers.Count - 1);
                }
            }

            ActiveWorkers = _workers.Count;
        }
    }

    /// <summary>
    /// The logic for a single worker thread.
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken workerCt)
    {
        var workerId = Guid.NewGuid().ToString()[..4]; // Short ID for logs
        _logger.LogInformation("[Worker {ID}] Started.", workerId);

        try
        {
            // workerCt is the personal cancellation token for this specific worker.
            // If we Scale Down, this token is cancelled.

            while (!workerCt.IsCancellationRequested)
            {
                // 1. Wait for work.
                // If cancellation is requested (Scale Down), WaitToReadAsync throws OperationCanceledException
                // or returns false, exiting the loop gracefully.
                if (await _queue.Reader.WaitToReadAsync(workerCt))
                {
                    while (_queue.Reader.TryRead(out var job))
                    {
                        // 2. If we grabbed a job, process it FULLY.
                        // We use CancellationToken.None here to ensure the job is not interrupted 
                        // mid-process if the worker is scaled down.

                        try
                        {
                            await ProcessSingleJobAsync(job, workerId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Worker {ID}] Job {JobId} failed critical wrapper", workerId, job.Id);
                        }

                        // Check if we were fired after finishing the job
                        if (workerCt.IsCancellationRequested) break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal behavior during Scale Down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Worker {ID}] Loop crashed unexpectedly.", workerId);
        }
        finally
        {
            _logger.LogInformation("[Worker {ID}] Stopped (Graceful shutdown).", workerId);
        }
    }

    private async Task ProcessSingleJobAsync(IChokaQJob job, string workerId)
    {
        // Log Start
        await UpdateStateAndNotifyAsync(job.Id, JobStatus.Processing);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobType = job.GetType();

            // Resolve generic handler IChokaQJobHandler<T>
            var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
            var handler = scope.ServiceProvider.GetService(handlerType);

            if (handler == null) throw new InvalidOperationException($"No handler for {jobType.Name}");

            var method = handlerType.GetMethod("HandleAsync");
            if (method != null)
            {
                // Execute handler logic
                await (Task)method.Invoke(handler, new object[] { job, CancellationToken.None })!;
            }

            await UpdateStateAndNotifyAsync(job.Id, JobStatus.Succeeded);
            _logger.LogInformation("[Worker {ID}] Job {JobId} done.", workerId, job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Worker {ID}] Job {JobId} failed.", workerId, job.Id);
            await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed);
        }
    }

    private async Task UpdateStateAndNotifyAsync(string jobId, JobStatus status)
    {
        await _storage.UpdateJobStateAsync(jobId, status);
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, status);
        }
        catch { }
    }
}