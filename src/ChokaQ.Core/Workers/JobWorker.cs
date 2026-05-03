using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
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
    private readonly JobTypeRegistry _registry;
    private readonly TimeSpan _pausedQueuePollingDelay;
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
    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

    public JobWorker(
        InMemoryQueue queue,
        IJobStorage storage,
        ILogger<JobWorker> logger,
        IJobStateManager stateManager,
        IJobProcessor processor,
        JobTypeRegistry registry,
        ChokaQOptions? options = null)
    {
        _queue = queue;
        _storage = storage;
        _logger = logger;
        _stateManager = stateManager;
        _processor = processor;
        _registry = registry;
        _pausedQueuePollingDelay = (options ?? new ChokaQOptions()).Worker.PausedQueuePollingDelay;
    }

    public async Task CancelJobAsync(string jobId, string? actor = null)
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
            _logger.LogWarning(
                ChokaQLogEvents.AdminCommandRejected,
                "Cannot cancel job {JobId}: not found in Hot table.",
                jobId);
            return;
        }

        // Only cancel if Pending or Fetched (Processing is handled by CancelJob above)
        if (job.Status == JobStatus.Pending || job.Status == JobStatus.Fetched)
        {
            _logger.LogInformation(
                ChokaQLogEvents.AdminCommandCompleted,
                "Archiving pending job {JobId} to DLQ as Cancelled.",
                jobId);
            // The actor comes from the admin surface (The Deck or host API). Keeping it in the
            // cancellation details gives operators an audit trail for destructive interventions.
            await _stateManager.ArchiveCancelledAsync(
                jobId, job.Type, job.Queue, ChokaQ.Abstractions.Enums.JobCancellationReason.Admin, actor ?? "Admin cancellation");
        }
    }

    public async Task RestartJobAsync(string jobId, string? actor = null)
    {
        // First check Hot table
        var hotJob = await _storage.GetJobAsync(jobId);
        if (hotJob != null)
        {
            if (hotJob.Status == JobStatus.Processing || hotJob.Status == JobStatus.Pending)
            {
                _logger.LogWarning(
                    ChokaQLogEvents.AdminCommandRejected,
                    "Cannot restart job {JobId}: still active ({Status}).",
                    jobId,
                    hotJob.Status);
                return;
            }
        }

        // Check DLQ for resurrection
        var dlqJob = await _storage.GetDLQJobAsync(jobId);
        if (dlqJob != null)
        {
            _logger.LogInformation(
                ChokaQLogEvents.AdminCommandCompleted,
                "Resurrecting job {JobId} from DLQ.",
                jobId);
            await _storage.ResurrectAsync(jobId, null, actor ?? "Admin restart");

            // Re-queue for processing
            await RequeueJobFromStorage(jobId);
            return;
        }

        // Check Archive for clone/restart
        var archivedJob = await _storage.GetArchiveJobAsync(jobId);
        if (archivedJob != null)
        {
            _logger.LogInformation(
                ChokaQLogEvents.AdminCommandRejected,
                "Cannot restart succeeded job {JobId} from Archive. Use clone functionality.",
                jobId);
            return;
        }

        _logger.LogWarning(
            ChokaQLogEvents.AdminCommandRejected,
            "Cannot restart job {JobId}: not found in any table.",
            jobId);
    }

    public async Task SetJobPriorityAsync(string jobId, int priority, string? actor = null)
    {
        var result = await _storage.UpdateJobDataAsync(
            jobId,
            new Abstractions.DTOs.JobDataUpdateDto(null, null, priority),
            actor ?? "Admin");

        if (!result)
        {
            _logger.LogWarning(
                ChokaQLogEvents.AdminCommandRejected,
                "Cannot update priority for job {JobId}: not Pending or not found.",
                jobId);
        }
    }

    /// <summary>
    /// Batch cancellation via parallel Task.WhenAll.
    /// </summary>
    public Task CancelJobsAsync(IEnumerable<string> jobIds, string? actor = null) =>
        Task.WhenAll(jobIds.Select(jobId => CancelJobAsync(jobId, actor)));

    /// <summary>
    /// Batch restart: uses ResurrectBatchAsync for single DB roundtrip, then re-queues.
    /// </summary>
    public async Task RestartJobsAsync(IEnumerable<string> jobIds, string? actor = null)
    {
        var ids = jobIds.ToArray();
        var resurrected = await _storage.ResurrectBatchAsync(ids, actor ?? "Admin restart");
        _logger.LogInformation(
            ChokaQLogEvents.AdminCommandCompleted,
            "Bulk restart: {Count}/{Total} jobs resurrected from DLQ.",
            resurrected,
            ids.Length);

        // Re-queue all resurrected jobs for the in-memory channel
        foreach (var id in ids) await RequeueJobFromStorage(id);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsRunning = true;
        RecordHeartbeat();
        UpdateWorkerCount(1);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        await base.StopAsync(cancellationToken);
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
                // This heartbeat is process liveness, not job liveness. It lets host health checks
                // detect a worker loop that stopped cycling even when no jobs are currently active.
                RecordHeartbeat();

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
                            // The delay prevents a paused in-memory queue from spinning hot.
                            // It is configurable so test hosts can shorten it and production
                            // hosts can tune the balance between pause responsiveness and CPU use.
                            await Task.Delay(_pausedQueuePollingDelay, workerCt);
                            continue;
                        }

                        // The persisted Type column and the dispatcher key must be the same value.
                        // Profiles often map a CLR type to a stable public key such as "email_v1";
                        // using the class name here would make in-memory Bus mode behave differently
                        // from SQL mode and break dispatch for registered custom keys.
                        var typeKey = _registry.GetKeyByType(job.GetType()) ?? job.GetType().Name;
                        var payload = JsonSerializer.Serialize(job, job.GetType());

                        await _processor.ProcessJobAsync(
                            job.Id,
                            typeKey,
                            payload,
                            workerId,
                            storageJob.AttemptCount,
                            storageJob.CreatedBy,
                            storageJob.ScheduledAtUtc,
                            storageJob.CreatedAtUtc,
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
            _logger.LogInformation(
                ChokaQLogEvents.WorkerStopped,
                "[Worker {ID}] Stopped.",
                workerId);
        }
    }

    private async Task RequeueJobFromStorage(string jobId)
    {
        var job = await _storage.GetJobAsync(jobId);
        if (job == null) return;

        // Try to reconstruct job object for channel
        // Stored job.Type is the public dispatch key, not necessarily a CLR type name.
        // Resolve through the registry first so DLQ resurrection works for profile keys
        // exactly the same way as fresh enqueue and SQL polling.
        var jobType = _registry.GetTypeByKey(job.Type) ?? Type.GetType(job.Type);
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

        _logger.LogWarning(
            ChokaQLogEvents.StateTransitionNotApplied,
            "Could not requeue job {JobId}: type resolution failed.",
            jobId);
    }

    private void RecordHeartbeat() => LastHeartbeatUtc = DateTimeOffset.UtcNow;
}
