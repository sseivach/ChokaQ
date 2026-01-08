using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService, IWorkerManager
{
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<JobWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICircuitBreaker _breaker;

    // Configurable Properties
    public int MaxRetries { get; set; } = 3;

    // Default base delay in seconds
    public int RetryDelaySeconds { get; set; } = 3;

    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
    private readonly object _lock = new();

    // Registry of tokens for currently running jobs to allow cancellation
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int ActiveWorkers { get; private set; } = 0;

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<JobWorker> logger,
        IServiceScopeFactory scopeFactory,
        ICircuitBreaker breaker)
    {
        _queue = queue;
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _breaker = breaker;
    }

    // Implementation of CancelJobAsync
    public async Task CancelJobAsync(string jobId)
    {
        // 1. If the job is currently running, cancel the token
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel(); // This triggers the CancellationToken passed to the handler
        }
        else
        {
            // 2. If not running (e.g., Pending in queue), just update the storage
            // When the worker eventually picks it up, it will check the status and skip.
            _logger.LogInformation("Marking pending job {JobId} as Cancelled.", jobId);
            // We don't know the exact type here easily without storage lookup, 
            // passing "Unknown" or generic name is acceptable for this edge case notification.
            await UpdateStateAndNotifyAsync(jobId, "Job", JobStatus.Cancelled, 0);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateWorkerCount(1);
        return Task.CompletedTask;
    }

    public void UpdateWorkerCount(int targetCount)
    {
        if (targetCount < 0) targetCount = 0;
        if (targetCount > 100) targetCount = 100;

        lock (_lock)
        {
            int current = _workers.Count;

            if (targetCount > current)
            {
                int toAdd = targetCount - current;
                for (int i = 0; i < toAdd; i++)
                {
                    var cts = new CancellationTokenSource();
                    var task = Task.Run(() => WorkerLoopAsync(cts.Token), cts.Token);
                    _workers.Add((task, cts));
                }
            }
            else if (targetCount < current)
            {
                int toRemove = current - targetCount;
                for (int i = 0; i < toRemove; i++)
                {
                    var worker = _workers.Last();
                    worker.Cts.Cancel();
                    _workers.RemoveAt(_workers.Count - 1);
                }
            }
            ActiveWorkers = _workers.Count;
        }
    }

    private async Task WorkerLoopAsync(CancellationToken workerCt)
    {
        var workerId = Guid.NewGuid().ToString()[..4];

        try
        {
            while (!workerCt.IsCancellationRequested)
            {
                if (await _queue.Reader.WaitToReadAsync(workerCt))
                {
                    while (_queue.Reader.TryRead(out var job))
                    {
                        try
                        {
                            await ProcessSingleJobAsync(job, workerId, workerCt);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Worker {ID}] Critical wrapper error", workerId);
                        }

                        if (workerCt.IsCancellationRequested) break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _logger.LogInformation("[Worker {ID}] Stopped.", workerId);
        }
    }

    private async Task ProcessSingleJobAsync(IChokaQJob job, string workerId, CancellationToken workerCt)
    {
        var storageDto = await _storage.GetJobAsync(job.Id);

        // Check if already cancelled while in queue
        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, job.Id);
            return;
        }

        int currentAttempt = storageDto?.AttemptCount ?? 1;
        var jobTypeName = job.GetType().Name; // Used for UI and Circuit Breaker

        // [STEP 1] Check Circuit Breaker status
        if (!_breaker.IsExecutionPermitted(jobTypeName))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", job.Id, jobTypeName);
            await Task.Delay(5000, workerCt);
            await _queue.RequeueAsync(job, workerCt);
            return;
        }

        // Pass jobTypeName to notifier
        await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Processing, currentAttempt);

        // Create a specific cancellation token for this job execution
        // It triggers if:
        // 1. The Worker shuts down (workerCt)
        // 2. Someone clicks "Stop" (jobCts)
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);

        // Register the token so we can cancel it externally
        _activeJobTokens.TryAdd(job.Id, jobCts);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobContext = scope.ServiceProvider.GetRequiredService<JobContext>();
            jobContext.JobId = job.Id;

            var jobType = job.GetType();
            var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
            var handler = scope.ServiceProvider.GetService(handlerType);

            if (handler == null) throw new InvalidOperationException($"No handler for {jobType.Name}");

            var method = handlerType.GetMethod("HandleAsync");
            if (method != null)
            {
                // Pass our specific job cancellation token
                await (Task)method.Invoke(handler, new object[] { job, jobCts.Token })!;
            }

            // [STEP 2] Report Success
            _breaker.ReportSuccess(jobTypeName);

            // Pass jobTypeName
            await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Succeeded, currentAttempt);
            _logger.LogInformation("[Worker {ID}] Job {JobId} done.", workerId, job.Id);
        }
        catch (OperationCanceledException)
        {
            // Handle specific cancellation
            _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED.", workerId, job.Id);
            await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Cancelled, currentAttempt);
        }
        catch (Exception ex)
        {
            // [STEP 3] Report Failure
            _breaker.ReportFailure(jobTypeName);

            // Check if it was an inner cancellation exception wrapped in TargetInvocationException
            if (ex.InnerException is OperationCanceledException)
            {
                _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED (Wrapped).", workerId, job.Id);
                await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Cancelled, currentAttempt);
                return;
            }

            storageDto = await _storage.GetJobAsync(job.Id);
            if (storageDto == null || storageDto.Status == JobStatus.Cancelled) return;

            currentAttempt = storageDto.AttemptCount;

            if (currentAttempt <= MaxRetries)
            {
                int nextAttempt = currentAttempt + 1;

                // Smart Delay: Exponential Backoff + Jitter
                var baseDelay = RetryDelaySeconds * Math.Pow(2, currentAttempt - 1);
                var jitter = Random.Shared.Next(0, 1000);
                var totalDelayMs = (int)(baseDelay * 1000) + jitter;

                _logger.LogWarning(ex, "[Worker {ID}] Failed. Retrying in {Delay}ms (Attempt {Next}).",
                    workerId, totalDelayMs, nextAttempt);

                await _storage.IncrementJobAttemptAsync(job.Id, nextAttempt);
                await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Pending, nextAttempt);

                await Task.Delay(totalDelayMs, workerCt);
                await _queue.RequeueAsync(job, workerCt);
            }
            else
            {
                _logger.LogError(ex, "[Worker {ID}] FAILED permanently.", workerId);
                await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Failed, currentAttempt);
            }
        }
        finally
        {
            // Cleanup token from registry
            _activeJobTokens.TryRemove(job.Id, out _);
        }
    }

    // Added 'type' parameter
    private async Task UpdateStateAndNotifyAsync(string jobId, string type, JobStatus status, int attemptCount)
    {
        await _storage.UpdateJobStateAsync(jobId, status);
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, type, status, attemptCount);
        }
        catch { /* Ignore notification failures */ }
    }
}