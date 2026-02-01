using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
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
/// 
/// Three Pillars integration:
/// - Reads from Hot table via storage
/// - Cancellation archives to DLQ
/// - Resurrection happens via storage.ResurrectAsync
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
    private int _targetWorkerCount = 1;
    public int TotalWorkers => _targetWorkerCount;

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

        // Fetch job from Hot table
        var job = await _storage.GetJobAsync(jobId);
        if (job == null)
        {
            _logger.LogWarning("Cannot cancel job {JobId}: not found in Hot table.", jobId);
            return;
        }

        // Only cancel if Pending or Fetched (Processing is handled by CancelJob above)
        if (job.Status == JobStatus.Pending || job.Status == JobStatus.Fetched)
        {
            _logger.LogInformation("Archiving pending job {JobId} to DLQ as Cancelled.", jobId);
            await _stateManager.ArchiveCancelledAsync(
                jobId, job.Type, job.Queue, "Admin cancellation");
        }
    }

    public async Task RestartJobAsync(string jobId)
    {
        // First check Hot table
        var hotJob = await _storage.GetJobAsync(jobId);
        if (hotJob != null)
        {
            if (hotJob.Status == JobStatus.Processing || hotJob.Status == JobStatus.Pending)
            {
                _logger.LogWarning("Cannot restart job {JobId}: still active ({Status}).", jobId, hotJob.Status);
                return;
            }
        }

        // Check DLQ for resurrection
        var dlqJob = await _storage.GetDLQJobAsync(jobId);
        if (dlqJob != null)
        {
            _logger.LogInformation("Resurrecting job {JobId} from DLQ...", jobId);
            await _storage.ResurrectAsync(jobId, null, "Admin restart");

            // Re-queue for processing
            await RequeueJobFromStorage(jobId);
            return;
        }

        // Check Archive for clone/restart
        var archivedJob = await _storage.GetArchiveJobAsync(jobId);
        if (archivedJob != null)
        {
            _logger.LogInformation("Cannot restart succeeded job {JobId} from Archive. Use clone functionality.", jobId);
            return;
        }

        _logger.LogWarning("Cannot restart job {JobId}: not found in any table.", jobId);
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        var result = await _storage.UpdateJobDataAsync(
            jobId,
            new Abstractions.DTOs.JobDataUpdateDto(null, null, priority),
            "Admin");

        if (!result)
        {
            _logger.LogWarning("Cannot update priority for job {JobId}: not Pending or not found.", jobId);
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

        _targetWorkerCount = targetCount;

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
                        // Look up job in Hot table
                        var storageJob = await _storage.GetJobAsync(job.Id, workerCt);
                        if (storageJob == null) continue;

                        // Check if queue is paused
                        var queues = await _storage.GetQueuesAsync(workerCt);
                        var targetQueue = queues.FirstOrDefault(q => q.Name == storageJob.Queue);

                        if (targetQueue != null && targetQueue.IsPaused)
                        {
                            // Queue is paused - requeue
                            await _queue.RequeueAsync(job, workerCt);
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
                            storageJob.AttemptCount,
                            storageJob.CreatedBy,
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

    private async Task RequeueJobFromStorage(string jobId)
    {
        var job = await _storage.GetJobAsync(jobId);
        if (job == null) return;

        // Try to reconstruct job object for channel
        var jobType = Type.GetType(job.Type);
        if (jobType == null)
        {
            jobType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .FirstOrDefault(t => t.Name == job.Type);
        }

        if (jobType != null && job.Payload != null)
        {
            var jobObject = JsonSerializer.Deserialize(job.Payload, jobType) as IChokaQJob;
            if (jobObject != null)
            {
                await _queue.RequeueAsync(jobObject);
                return;
            }
        }

        _logger.LogWarning("Could not requeue job {JobId}: type resolution failed.", jobId);
    }
}
