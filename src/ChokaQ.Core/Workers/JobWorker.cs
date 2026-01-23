using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Core.Workers;

/// <summary>
/// In-Memory Worker implementation used for development or non-persistent scenarios.
/// Consumes jobs from System.Threading.Channels.
/// </summary>
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

        // Fetch metadata to keep state consistent
        var job = await _storage.GetJobAsync(jobId);

        await _stateManager.UpdateStateAsync(
            jobId,
            job?.Type ?? "Unknown",
            JobStatus.Cancelled,
            job?.AttemptCount ?? 0,
            executionDurationMs: null,
            createdBy: job?.CreatedBy,
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
            _logger.LogInformation("Restarting job {JobId}...", jobId);

            // 1. Reset State
            await _storage.UpdateJobStateAsync(jobId, JobStatus.Pending);
            await _storage.IncrementJobAttemptAsync(jobId, 0);

            await _stateManager.UpdateStateAsync(
                jobId,
                storageDto.Type,
                JobStatus.Pending,
                0,
                executionDurationMs: null,
                createdBy: storageDto.CreatedBy,
                startedAtUtc: null);

            // 2. Re-queue logic depends on strategy
            var jobType = Type.GetType(storageDto.Type);

            // Fallback: try to find type in current AppDomain assemblies if Type.GetType fails (often needed for simple names)
            if (jobType == null)
            {
                jobType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.Name == storageDto.Type);
            }

            if (jobType != null)
            {
                var jobObject = JsonSerializer.Deserialize(storageDto.Payload, jobType) as IChokaQJob;
                if (jobObject != null)
                {
                    await _queue.RequeueAsync(jobObject);
                }
            }
            else
            {
                _logger.LogWarning("Could not resolve type {Type} for In-Memory restart. Job is reset in DB but not in Channel.", storageDto.Type);
            }
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
                        // Since IChokaQJob doesn't know its queue, we look it up in storage.
                        var storageJob = await _storage.GetJobAsync(job.Id, workerCt);

                        // If job vanished or invalid, skip
                        if (storageJob == null) continue;

                        var queues = await _storage.GetQueuesAsync(workerCt);
                        var targetQueue = queues.FirstOrDefault(q => q.Name == storageJob.Queue);

                        if (targetQueue != null && targetQueue.IsPaused)
                        {
                            // Queue is paused! Put it back at the end of the line.
                            await _queue.RequeueAsync(job, workerCt);

                            // Sleep briefly to avoid hot-looping (consuming CPU while requeuing same job)
                            await Task.Delay(1000, workerCt);
                            continue;
                        }

                        var typeKey = job.GetType().Name;

                        var payload = JsonSerializer.Serialize(job, job.GetType());

                        await _processor.ProcessJobAsync(
                            job.Id,
                            typeKey,
                            payload,
                            workerId,
                            workerCt
                        );

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

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        await _storage.UpdateJobPriorityAsync(jobId, priority);
    }
}