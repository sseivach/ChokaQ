using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Queues;
using ChokaQ.Core.Resilience;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService, IWorkerManager
{
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<JobWorker> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobExecutor _executor;

    // Configurable Properties
    public int MaxRetries { get; set; } = 3;
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
        ICircuitBreaker breaker,
        IJobExecutor executor)
    {
        _queue = queue;
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
        _breaker = breaker;
        _executor = executor;
    }

    // Implementation of CancelJobAsync
    public async Task CancelJobAsync(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel();
        }
        else
        {
            _logger.LogInformation("Marking pending job {JobId} as Cancelled.", jobId);
            await UpdateStateAndNotifyAsync(jobId, "Job", JobStatus.Cancelled, 0);
        }
    }

    // Implementation of RestartJobAsync
    public async Task RestartJobAsync(string jobId)
    {
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null) return;

        if (storageDto.Status == JobStatus.Processing || storageDto.Status == JobStatus.Pending)
        {
            _logger.LogWarning("Cannot restart job {JobId}: it is currently active ({Status}).", jobId, storageDto.Status);
            return;
        }

        try
        {
            var jobType = Type.GetType(storageDto.Type);
            if (jobType == null) throw new InvalidOperationException($"Cannot load type '{storageDto.Type}'.");

            var jobObject = JsonSerializer.Deserialize(storageDto.Payload, jobType) as IChokaQJob;
            if (jobObject == null) throw new InvalidOperationException("Failed to deserialize job payload.");

            _logger.LogInformation("Restarting job {JobId}...", jobId);

            await _storage.UpdateJobStateAsync(jobId, JobStatus.Pending);
            await _storage.IncrementJobAttemptAsync(jobId, 0);

            await UpdateStateAndNotifyAsync(jobId, jobObject.GetType().Name, JobStatus.Pending, 0);
            await _queue.RequeueAsync(jobObject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart job {JobId}.", jobId);
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

        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, job.Id);
            return;
        }

        int currentAttempt = storageDto?.AttemptCount ?? 1;
        var jobTypeName = job.GetType().Name;

        if (!_breaker.IsExecutionPermitted(jobTypeName))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", job.Id, jobTypeName);
            await Task.Delay(5000, workerCt);
            await _queue.RequeueAsync(job, workerCt);
            return;
        }

        await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Processing, currentAttempt);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(job.Id, jobCts);

        try
        {
            await _executor.ExecuteJobAsync(job, jobCts.Token);

            _breaker.ReportSuccess(jobTypeName);
            await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Succeeded, currentAttempt);
            _logger.LogInformation("[Worker {ID}] Job {JobId} done.", workerId, job.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED.", workerId, job.Id);
            await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Cancelled, currentAttempt);
        }
        catch (Exception ex)
        {
            _breaker.ReportFailure(jobTypeName);

            if (ex is OperationCanceledException)
            {
                _logger.LogWarning("[Worker {ID}] Job {JobId} was CANCELLED.", workerId, job.Id);
                await UpdateStateAndNotifyAsync(job.Id, jobTypeName, JobStatus.Cancelled, currentAttempt);
                return;
            }

            storageDto = await _storage.GetJobAsync(job.Id);
            if (storageDto == null || storageDto.Status == JobStatus.Cancelled) return;

            currentAttempt = storageDto.AttemptCount;

            if (currentAttempt <= MaxRetries)
            {
                int nextAttempt = currentAttempt + 1;

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
            _activeJobTokens.TryRemove(job.Id, out _);
        }
    }

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