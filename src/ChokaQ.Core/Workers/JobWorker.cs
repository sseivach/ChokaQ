using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Processing;
using ChokaQ.Core.Queues;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Core.Workers;

public class JobWorker : BackgroundService, IWorkerManager
{
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly ILogger<JobWorker> _logger;
    private readonly IJobStateManager _stateManager;
    private readonly IJobProcessor _processor;
    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
    private readonly object _lock = new();

    // Configuration delegated to Processor
    public int MaxRetries
    {
        get => _processor.MaxRetries;
        set => _processor.MaxRetries = value;
    }

    public int RetryDelaySeconds
    {
        get => _processor.RetryDelaySeconds;
        set => _processor.RetryDelaySeconds = value;
    }

    public int ActiveWorkers { get; private set; } = 0;

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        ILogger<JobWorker> logger,
        IJobStateManager stateManager,
        IJobProcessor processor)
    {
        _queue = queue;
        _storage = storage;
        _logger = logger;
        _stateManager = stateManager;
        _processor = processor;
    }

    public async Task CancelJobAsync(string jobId)
    {
        // Try to cancel if running
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        // Also ensure state is updated if it was pending
        _logger.LogInformation("Marking job {JobId} as Cancelled (if not already).", jobId);

        // UPDATE STATE: Cancelled
        await _stateManager.UpdateStateAsync(
            jobId,
            "Job",
            JobStatus.Cancelled,
            0,
            executionDurationMs: null,
            createdBy: null,
            startedAtUtc: null);
    }

    public async Task RestartJobAsync(string jobId)
    {
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null) return;

        if (storageDto.Status == JobStatus.Processing || storageDto.Status == JobStatus.Pending)
        {
            _logger.LogWarning("Cannot restart job {JobId}: active ({Status}).", jobId, storageDto.Status);
            return;
        }

        try
        {
            var jobType = Type.GetType(storageDto.Type);
            if (jobType == null) throw new InvalidOperationException($"Cannot load type '{storageDto.Type}'.");

            var jobObject = JsonSerializer.Deserialize(storageDto.Payload, jobType) as IChokaQJob;
            if (jobObject == null) throw new InvalidOperationException("Failed to deserialize payload.");

            _logger.LogInformation("Restarting job {JobId}...", jobId);

            await _storage.UpdateJobStateAsync(jobId, JobStatus.Pending);
            await _storage.IncrementJobAttemptAsync(jobId, 0);

            // UPDATE STATE: Pending
            await _stateManager.UpdateStateAsync(
                jobId,
                jobObject.GetType().Name,
                JobStatus.Pending,
                0,
                executionDurationMs: null,
                createdBy: storageDto.CreatedBy,
                startedAtUtc: null);

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
                        // All complex logic is now delegated to the processor
                        await _processor.ProcessJobAsync(job, workerId, workerCt);
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
}