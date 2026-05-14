using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Serialization;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Defaults;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
internal class JobWorker : BackgroundService, IWorkerManager
{
    private readonly InMemoryQueue _queue;
    private readonly IJobStorage _storage;
    private readonly ILogger<JobWorker> _logger;
    private readonly IJobStateManager _stateManager;
    private readonly IJobProcessor _processor;
    private readonly JobTypeRegistry _registry;
    private readonly IChokaQJobSerializer _serializer;
    private readonly TimeSpan _pausedQueuePollingDelay;
    private readonly TimeSpan _shutdownGracePeriod;
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
        IChokaQJobSerializer serializer,
        ChokaQOptions? options = null)
    {
        _queue = queue;
        _storage = storage;
        _logger = logger;
        _stateManager = stateManager;
        _processor = processor;
        _registry = registry;
        _serializer = serializer;
        var resolvedOptions = options ?? new ChokaQOptions();
        _pausedQueuePollingDelay = resolvedOptions.Worker.PausedQueuePollingDelay;
        _shutdownGracePeriod = resolvedOptions.Worker.ShutdownGracePeriod;
    }

    public async Task CancelJobAsync(string jobId, string? actor = null)
    {
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

        if (job.Status == JobStatus.Processing && _processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelRunningJob(jobId);
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
            if (hotJob.Status == JobStatus.Pending || hotJob.Status == JobStatus.Fetched || hotJob.Status == JobStatus.Processing)
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

            var requeueResult = await RequeueJobFromStorage(jobId);
            LogSingleRequeueResult(jobId, requeueResult);
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

        var requeueResults = new Dictionary<InMemoryRequeueResult, int>();

        foreach (var id in ids)
        {
            var result = await RequeueJobFromStorage(id);
            requeueResults[result] = requeueResults.GetValueOrDefault(result) + 1;
        }

        _logger.LogInformation(
            ChokaQLogEvents.AdminCommandCompleted,
            "Bulk restart in-memory requeue results: {Requeued} requeued, {MissingHotRow} missing Hot row, {NotPending} not Pending, {UnknownType} unknown type, {EmptyPayload} empty payload, {Failed} failed.",
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.Requeued),
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.HotRowMissing),
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.NotPending),
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.UnknownType),
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.EmptyPayload),
            requeueResults.GetValueOrDefault(InMemoryRequeueResult.Failed));
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
        List<(Task Task, CancellationTokenSource Cts)> workers;
        lock (_lock)
        {
            workers = _workers.ToList();
            _workers.Clear();
            ActiveWorkers = 0;
            _targetWorkerCount = 0;
        }

        foreach (var worker in workers)
        {
            worker.Cts.Cancel();
        }

        await WaitForWorkerTasksAsync(workers.Select(worker => worker.Task).ToArray(), cancellationToken);

        foreach (var worker in workers)
        {
            worker.Cts.Dispose();
        }

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

                        // In-memory execution is channel-driven, with Hot storage acting as the
                        // control row. A buffered channel item is safe to execute only while the
                        // persisted row is still the same Pending job accepted by enqueue/restart.
                        // If an admin cancel, DLQ move, edit, or earlier duplicate already changed
                        // the row, this item is stale and must not dispatch user code.
                        var typeKey = _registry.GetPersistedTypeKey(job.GetType());
                        var payload = _serializer.Serialize(job, job.GetType());

                        if (!CanExecuteChannelItem(job.Id, storageJob, typeKey, payload))
                        {
                            continue;
                        }

                        // Check if queue is paused. This is intentionally a low-throughput
                        // development-mode check; SQL mode owns high-throughput queue policy.
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

                        await _processor.ProcessJobAsync(
                            job.Id,
                            typeKey,
                            payload,
                            null,
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

    private bool CanExecuteChannelItem(string jobId, JobHotEntity storageJob, string typeKey, string payload)
    {
        if (storageJob.Status != JobStatus.Pending)
        {
            _logger.LogInformation(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped stale in-memory channel item {JobId}: Hot row status is {Status}, not Pending.",
                jobId,
                storageJob.Status);
            return false;
        }

        if (!string.Equals(storageJob.Type, typeKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped stale in-memory channel item {JobId}: channel type key {ChannelType} does not match Hot row type key {StorageType}.",
                jobId,
                typeKey,
                storageJob.Type);
            return false;
        }

        if (!string.Equals(storageJob.Payload, payload, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped stale in-memory channel item {JobId}: channel payload no longer matches the Hot row payload.",
                jobId);
            return false;
        }

        return true;
    }

    private async Task<InMemoryRequeueResult> RequeueJobFromStorage(string jobId)
    {
        var job = await _storage.GetJobAsync(jobId);
        if (job == null) return InMemoryRequeueResult.HotRowMissing;

        if (job.Status != JobStatus.Pending)
        {
            return InMemoryRequeueResult.NotPending;
        }

        // Try to reconstruct job object for channel
        // Stored job.Type is the public dispatch key, not necessarily a CLR type name.
        // Resolve through the registry first so DLQ resurrection works for profile keys
        // exactly the same way as fresh enqueue and SQL polling.
        var jobType = _registry.ResolvePersistedType(job.Type);

        if (jobType == null)
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Could not requeue job {JobId}: unknown job type key '{JobType}'. Register the type or store an assembly-qualified fallback identity.",
                jobId,
                job.Type);
            return InMemoryRequeueResult.UnknownType;
        }

        if (string.IsNullOrWhiteSpace(job.Payload))
        {
            return InMemoryRequeueResult.EmptyPayload;
        }

        try
        {
            var jobObject = _serializer.Deserialize(job.Payload, jobType);
            if (jobObject == null)
            {
                return InMemoryRequeueResult.EmptyPayload;
            }

            await _queue.RequeueAsync(jobObject);
            return InMemoryRequeueResult.Requeued;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                ex,
                "Could not requeue job {JobId}: payload deserialization failed for type key '{JobType}'.",
                jobId,
                job.Type);
            return InMemoryRequeueResult.Failed;
        }
    }

    private void LogSingleRequeueResult(string jobId, InMemoryRequeueResult result)
    {
        if (result == InMemoryRequeueResult.Requeued)
        {
            _logger.LogInformation(
                ChokaQLogEvents.AdminCommandCompleted,
                "Requeued resurrected in-memory job {JobId}.",
                jobId);
            return;
        }

        _logger.LogWarning(
            ChokaQLogEvents.AdminCommandRejected,
            "Resurrected job {JobId}, but in-memory channel requeue did not complete. Result: {Result}.",
            jobId,
            result);
    }

    private void RecordHeartbeat() => LastHeartbeatUtc = DateTimeOffset.UtcNow;

    private async Task WaitForWorkerTasksAsync(Task[] tasks, CancellationToken cancellationToken)
    {
        if (tasks.Length == 0)
        {
            return;
        }

        using var shutdownCts = new CancellationTokenSource(_shutdownGracePeriod);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownCts.Token);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ChokaQLogEvents.WorkerShutdownTimedOut,
                "In-memory worker shutdown timed out after {Timeout}. {Count} worker loop(s) may still be stopping.",
                _shutdownGracePeriod,
                tasks.Count(task => !task.IsCompleted));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ChokaQLogEvents.WorkerShutdownTimedOut,
                "In-memory worker shutdown was interrupted by the host cancellation token. {Count} worker loop(s) may still be stopping.",
                tasks.Count(task => !task.IsCompleted));
        }
    }

    private enum InMemoryRequeueResult
    {
        Requeued,
        HotRowMissing,
        NotPending,
        UnknownType,
        EmptyPayload,
        Failed
    }
}
