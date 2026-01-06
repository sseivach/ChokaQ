using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs; // Needed for DTO
using ChokaQ.Abstractions.Enums;
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

    // Retry Configuration
    private const int MaxRetries = 3;

    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
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
        UpdateWorkerCount(1);
        return Task.CompletedTask;
    }

    public void UpdateWorkerCount(int targetCount)
    {
        // (Оставляем код Scale Up/Down без изменений, он у тебя уже есть)
        if (targetCount < 0) targetCount = 0;
        if (targetCount > 10) targetCount = 10;

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
        await UpdateStateAndNotifyAsync(job.Id, JobStatus.Processing);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobType = job.GetType();
            var handlerType = typeof(IChokaQJobHandler<>).MakeGenericType(jobType);
            var handler = scope.ServiceProvider.GetService(handlerType);

            if (handler == null) throw new InvalidOperationException($"No handler for {jobType.Name}");

            var method = handlerType.GetMethod("HandleAsync");
            if (method != null)
            {
                await (Task)method.Invoke(handler, new object[] { job, CancellationToken.None })!;
            }

            await UpdateStateAndNotifyAsync(job.Id, JobStatus.Succeeded);
            _logger.LogInformation("[Worker {ID}] Job {JobId} done.", workerId, job.Id);
        }
        catch (Exception ex)
        {
            // --- RETRY LOGIC START ---

            // 1. Get current state to check attempts
            var storageDto = await _storage.GetJobAsync(job.Id);

            // If storage is gone or something weird happened, just fail
            if (storageDto == null)
            {
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed);
                return;
            }

            int currentAttempt = storageDto.AttemptCount;

            if (currentAttempt < MaxRetries)
            {
                int nextAttempt = currentAttempt + 1;

                // Exponential Backoff: 2s, 4s, 8s...
                var delaySeconds = Math.Pow(2, nextAttempt);

                _logger.LogWarning(ex,
                    "[Worker {ID}] Job {JobId} failed (Attempt {Attempt}/{Max}). Retrying in {Sec}s...",
                    workerId, job.Id, nextAttempt, MaxRetries, delaySeconds);

                // 2. Update counter in DB
                await _storage.IncrementJobAttemptAsync(job.Id, nextAttempt);

                // 3. Update Status back to Pending (so UI shows it's not dead)
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Pending);

                // 4. Wait (Blocking this worker thread - simplest implementation for now)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                // 5. Re-Enqueue (Push back to the channel)
                // Note: We push the SAME job object back.
                await _queue.EnqueueAsync(job);
            }
            else
            {
                // No more retries
                _logger.LogError(ex, "[Worker {ID}] Job {JobId} FAILED permanently after {Max} attempts.", workerId, job.Id, MaxRetries);
                await UpdateStateAndNotifyAsync(job.Id, JobStatus.Failed);
            }
            // --- RETRY LOGIC END ---
        }
    }

    private async Task UpdateStateAndNotifyAsync(string jobId, JobStatus status)
    {
        await _storage.UpdateJobStateAsync(jobId, status);
        try { await _notifier.NotifyJobUpdatedAsync(jobId, status); } catch { }
    }
}