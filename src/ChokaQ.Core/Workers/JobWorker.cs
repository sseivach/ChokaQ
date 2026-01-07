using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Contexts;
using ChokaQ.Core.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
                            await ProcessSingleJobAsync(job, workerId);
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

    private async Task ProcessSingleJobAsync(IChokaQJob job, string workerId)
    {
        var storageDto = await _storage.GetJobAsync(job.Id);
        int currentAttempt = storageDto?.AttemptCount ?? 1;
        var jobTypeName = job.GetType().Name; // Key for Circuit Breaker

        // [STEP 1] Check Circuit Breaker status
        if (!_breaker.IsExecutionPermitted(jobTypeName))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", job.Id, jobTypeName);

            // Requeue the job with a delay to prevent busy-waiting loop
            await Task.Delay(5000);
            await _queue.RequeueAsync(job);
            return;
        }

        await UpdateStateAndNotifyAsync(job.Id, JobStatus.Processing, currentAttempt);

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
                await (Task)method.Invoke(handler, new object[] { job, CancellationToken.None })!;
            }

            // [STEP 2] Report Success to Circuit Breaker
            _breaker.ReportSuccess(jobTypeName);

            await UpdateStateAndNotifyAsync(job.Id, JobStatus.Succeeded, currentAttempt);
            _logger.LogInformation("[Worker {ID}] Job {JobId} done (Attempt {Attempt}).", workerId, job.Id, currentAttempt);
        }
        catch (Exception ex)
        {
            // [STEP 3] Report Failure to Circuit Breaker
            _breaker.ReportFailure(jobTypeName);

            storageDto = await _storage.GetJobAsync(job.Id);
            if (storageDto == null)
            {
                // Job might have been deleted concurrently
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed, currentAttempt);
                return;
            }

            currentAttempt = storageDto.AttemptCount;

            if (currentAttempt <= MaxRetries)
            {
                int nextAttempt = currentAttempt + 1;

                // [STEP 4] Smart Delay: Exponential Backoff + Jitter
                // Formula: Delay * 2^(retry-1) + Random(0-1000ms)
                var baseDelay = RetryDelaySeconds * Math.Pow(2, currentAttempt - 1);
                var jitter = Random.Shared.Next(0, 1000); // Add randomness to prevent Thundering Herd
                var totalDelayMs = (int)(baseDelay * 1000) + jitter;

                _logger.LogWarning(ex, "[Worker {ID}] Job {JobId} failed (Attempt {Attempt}). Retrying in {Delay}ms...",
                    workerId, job.Id, currentAttempt, totalDelayMs);

                await _storage.IncrementJobAttemptAsync(job.Id, nextAttempt);
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Pending, nextAttempt);

                await Task.Delay(totalDelayMs); // Wait smartly
                await _queue.RequeueAsync(job);
            }
            else
            {
                _logger.LogError(ex, "[Worker {ID}] Job {JobId} FAILED permanently after {Attempt} attempts.", workerId, job.Id, currentAttempt);
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed, currentAttempt);
            }
        }
    }

    private async Task UpdateStateAndNotifyAsync(string jobId, JobStatus status, int attemptCount)
    {
        await _storage.UpdateJobStateAsync(jobId, status);
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, status, attemptCount);
        }
        catch { /* Ignore notification failures */ }
    }
}