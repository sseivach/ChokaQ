using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Concurrency;
using ChokaQ.Core.Observability;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Worker implementing the "Prefetching Consumer" pattern for SQL Server.
/// Decouples DB fetching latency from Job Execution throughput using an elastic concurrency limiter.
/// 
/// Three Pillars Integration:
/// - Fetches from JobsHot table
/// - Archives to JobsArchive or JobsDLQ via StateManager
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private const int PrefetchBufferCapacity = 100;

    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Internal buffer: Decouples Fetching (IO Bound) from Processing (CPU Bound)
    private readonly Channel<JobHotEntity> _prefetchBuffer;

    // Encapsulates the logic for dynamic scaling (Permit Burning / Minting)
    private readonly DynamicConcurrencyLimiter _concurrencyLimiter;

    // Shared cache for queue states. Updated by Fetcher, read by Processor.
    private readonly ConcurrentDictionary<string, bool> _queuePauseCache = new();

    private readonly object _processingTasksLock = new();
    private readonly HashSet<Task> _processingTasks = new();

    /// <summary>
    /// Gets the number of currently executing jobs.
    /// </summary>
    public int ActiveWorkers => _concurrencyLimiter.RunningCount;

    /// <summary>
    /// Gets the maximum concurrency limit.
    /// </summary>
    public int TotalWorkers => _concurrencyLimiter.Capacity;
    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

    // Delegate configuration to the central Processor
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

    public SqlJobWorker(
        IJobStorage storage,
        IJobProcessor processor,
        IJobStateManager stateManager,
        ILogger<SqlJobWorker> logger,
        SqlJobStorageOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SqlJobStorageOptions();

        // The SQL worker has two pressure boundaries:
        // 1. JobsHot is the durable backlog; producers finish once SQL commits the row.
        // 2. This bounded channel is only a local prefetch buffer; when it is full, the fetcher
        //    stops claiming more rows so memory usage stays bounded and SQL remains the backlog.
        _prefetchBuffer = Channel.CreateBounded<JobHotEntity>(new BoundedChannelOptions(PrefetchBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });

        // Initialize dynamic concurrency limiter with default capacity of 10
        _concurrencyLimiter = new DynamicConcurrencyLimiter(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsRunning = true;
        RecordHeartbeat();

        _logger.LogInformation(
            ChokaQLogEvents.WorkerStarted,
            "SQL Worker Starting. Strategy: Prefetch + DynamicConcurrencyLimiter. Initial Capacity: {Capacity}",
            _concurrencyLimiter.Capacity);

        // The hosted service must await the real loop tasks, not Task<Task> wrappers.
        // If ExecuteAsync returns while these loops keep running, the host believes SQL mode
        // has stopped even though background polling and job execution are still alive.
        var fetcherTask = FetcherLoopAsync(stoppingToken);
        var processorTask = ProcessorLoopAsync(stoppingToken);

        try
        {
            await Task.WhenAll(fetcherTask, processorTask);
        }
        finally
        {
            // Processing tasks own final state transitions. During shutdown we wait for them
            // so cancellations can be persisted as retry/DLQ decisions instead of becoming
            // unobserved fire-and-forget work after the host has already stopped the service.
            await WaitForProcessingTasksAsync();
            IsRunning = false;
        }
    }

    /// <summary>
    /// The "Supplier". Keeps the local buffer full of work from JobsHot table.
    /// Updates the local queue pause cache.
    /// </summary>
    private async Task FetcherLoopAsync(CancellationToken ct)
    {
        var workerId = $"{Environment.MachineName}-{Guid.NewGuid().ToString()[..4]}";

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // This is the process-level worker heartbeat used by host health checks. It is
                // intentionally separate from per-job heartbeats stored in JobsHot: no active jobs
                // should still produce a live worker signal while the fetcher is polling normally.
                RecordHeartbeat();

                try
                {
                    // Wait for space in buffer
                    if (!await _prefetchBuffer.Writer.WaitToWriteAsync(ct))
                        break;

                    // Determine batch size based on buffer space
                    int batchSize = Math.Min(20, PrefetchBufferCapacity - _prefetchBuffer.Reader.Count);
                    if (batchSize <= 0) batchSize = 1;

                    // Get queue settings from DB
                    var queues = await _storage.GetQueuesAsync(ct);

                    // Update local cache for the Processor loop
                    foreach (var q in queues)
                    {
                        _queuePauseCache[q.Name] = q.IsPaused;
                    }

                    // Filter only active queues for fetching
                    var activeQueues = queues.Where(q => !q.IsPaused).Select(q => q.Name).ToArray();

                    if (activeQueues.Length == 0)
                    {
                        _logger.LogDebug("No active queues. Fetcher sleeping...");
                        await Task.Delay(_options.NoQueuesSleepInterval, ct);
                        continue;
                    }

                    // Atomic Fetch & Lock from Hot table
                    var jobs = (await _storage.FetchNextBatchAsync(workerId, batchSize, activeQueues, ct)).ToArray();

                    if (jobs.Length == 0)
                    {
                        await Task.Delay(_options.PollingInterval, ct);
                        continue;
                    }

                    foreach (var job in jobs)
                    {
                        // Push to buffer - awaits if full (backpressure)
                        await _prefetchBuffer.Writer.WriteAsync(job, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ChokaQLogEvents.WorkerLoopCrashed,
                        ex,
                        "Fetcher Loop crashed. Cooling down.");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ChokaQLogEvents.WorkerLoopStoppedUnexpectedly,
                ex,
                "Fetcher Loop stopped unexpectedly.");
            throw;
        }
        finally
        {
            // Completing the writer is the hand-off between producer and consumer shutdown.
            // The processor loop can then drain/release any already-fetched jobs deterministically.
            _prefetchBuffer.Writer.TryComplete();
            _logger.LogInformation(
                ChokaQLogEvents.WorkerStopped,
                "Fetcher Loop Stopped.");
        }
    }

    /// <summary>
    /// The "Consumer". Orchestrates parallel execution limited by DynamicConcurrencyLimiter.
    /// Handles immediate release of jobs if the queue becomes paused while the job is in the buffer.
    /// </summary>
    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync())
            {
                if (ct.IsCancellationRequested)
                {
                    await ReleasePrefetchedJobAsync(job, "worker shutdown", CancellationToken.None);
                    continue;
                }

                // 1. Check if the queue is paused
                // If paused, we RELEASE the job back to the DB immediately.
                // We refresh the queue state here because a job can sit in the prefetch buffer
                // while an operator pauses the queue. The cached state from fetch time is not
                // strong enough to authorize starting new work after that operator decision.
                if (await IsQueuePausedAsync(job.Queue, ct))
                {
                    await ReleasePrefetchedJobAsync(job, $"queue {job.Queue} is paused", CancellationToken.None);
                    continue;
                }

                // 2. Wait for concurrency slot
                try
                {
                    await _concurrencyLimiter.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await ReleasePrefetchedJobAsync(job, "worker shutdown before execution slot", CancellationToken.None);
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    _concurrencyLimiter.Release();
                    await ReleasePrefetchedJobAsync(job, "worker shutdown before execution start", CancellationToken.None);
                    continue;
                }

                TrackProcessingTask(ProcessJobWithPermitAsync(job, ct));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal host shutdown path.
        }
        finally
        {
            _logger.LogInformation(
                ChokaQLogEvents.WorkerStopped,
                "Processor Loop Stopped.");
        }
    }

    private async Task<bool> IsQueuePausedAsync(string queueName, CancellationToken ct)
    {
        if (_queuePauseCache.TryGetValue(queueName, out var cachedPaused) && cachedPaused)
            return true;

        var queues = await _storage.GetQueuesAsync(ct);
        var paused = false;

        foreach (var queue in queues)
        {
            _queuePauseCache[queue.Name] = queue.IsPaused;
            if (string.Equals(queue.Name, queueName, StringComparison.Ordinal))
                paused = queue.IsPaused;
        }

        return paused;
    }

    private async Task ReleasePrefetchedJobAsync(JobHotEntity job, string reason, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug(
                ChokaQLogEvents.PrefetchedJobReleased,
                "Releasing prefetched job {JobId} from queue {Queue}: {Reason}.",
                job.Id, job.Queue, reason);

            // Prefetched jobs are still only claimed, not executing. Releasing them during
            // pause/shutdown shortens recovery time and teaches a key queue invariant:
            // do not start new work when the worker can no longer own its completion.
            await _storage.ReleaseJobAsync(job.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ChokaQLogEvents.PrefetchedJobReleaseFailed,
                ex,
                "Failed to release prefetched job {JobId}. It will be recovered by abandoned-job rescue.",
                job.Id);
        }
    }

    private async Task ProcessJobWithPermitAsync(JobHotEntity job, CancellationToken ct)
    {
        try
        {
            // Once a job starts executing, JobProcessor owns the lifecycle and final state
            // transition. The worker only supplies the lease identity and guarantees that the
            // task is observed during shutdown.
            await _processor.ProcessJobAsync(
                job.Id,
                job.Type,
                job.Payload ?? "{}",
                job.WorkerId ?? "sql-worker",
                job.AttemptCount,
                job.CreatedBy,
                job.ScheduledAtUtc,
                job.CreatedAtUtc,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                ChokaQLogEvents.ProcessingTaskShutdownObserved,
                "Processing task for job {JobId} observed worker shutdown.",
                job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ChokaQLogEvents.ProcessingTaskWrapperFailed,
                ex,
                "Critical error in job dispatch wrapper for {JobId}.",
                job.Id);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private void TrackProcessingTask(Task task)
    {
        lock (_processingTasksLock)
        {
            _processingTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_processingTasksLock)
                {
                    _processingTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForProcessingTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_processingTasksLock)
            {
                tasks = _processingTasks.ToArray();
            }

            if (tasks.Length == 0)
                return;

            _logger.LogInformation(
                ChokaQLogEvents.ProcessingTaskDrainStarted,
                "Waiting for {Count} active SQL processing task(s) to finish.",
                tasks.Length);
            await Task.WhenAll(tasks);
        }
    }

    private void RecordHeartbeat() => LastHeartbeatUtc = DateTimeOffset.UtcNow;

    // --- Management Interface Implementation ---

    public void UpdateWorkerCount(int count)
    {
        _logger.LogInformation(
            ChokaQLogEvents.WorkerCapacityChanged,
            "Updating worker count to {Count}",
            count);
        _concurrencyLimiter.SetCapacity(count);
    }

    public async Task CancelJobAsync(string jobId, string? actor = null)
    {
        // Try to cancel if running
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        // Also archive to DLQ if still in Hot and Pending/Fetched
        var job = await _storage.GetJobAsync(jobId);
        if (job != null && (job.Status == JobStatus.Pending || job.Status == JobStatus.Fetched))
        {
            // The worker manager is the write boundary for operator actions. Passing the actor
            // through cancellation details keeps destructive actions attributable in DLQ history.
            await _stateManager.ArchiveCancelledAsync(
                jobId,
                job.Type,
                job.Queue,
                ChokaQ.Abstractions.Enums.JobCancellationReason.Admin,
                actor ?? "Admin cancellation");
        }
    }

    public async Task RestartJobAsync(string jobId, string? actor = null)
    {
        // Check Hot table first
        var hotJob = await _storage.GetJobAsync(jobId);
        if (hotJob != null && hotJob.Status == JobStatus.Processing)
        {
            _logger.LogWarning(
                ChokaQLogEvents.AdminCommandRejected,
                "Cannot restart job {JobId}: still processing.",
                jobId);
            return;
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
            return;
        }

        _logger.LogWarning(
            ChokaQLogEvents.AdminCommandRejected,
            "Cannot restart job {JobId}: not found in DLQ.",
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
    /// <summary>
    /// Batch cancellation: stops running tasks in memory, then uses a single DB roundtrip 
    /// (via OPENJSON) to move all remaining Pending/Fetched jobs to the DLQ.
    /// </summary>
    public async Task CancelJobsAsync(IEnumerable<string> jobIds, string? actor = null)
    {
        var ids = jobIds.ToArray();
        if (ids.Length == 0) return;

        // 1. Cancel running tasks in memory (blazing fast, no DB call)
        if (_processor is JobProcessor concreteProcessor)
        {
            foreach (var id in ids)
            {
                concreteProcessor.CancelJob(id);
            }
        }

        // 2. Batch move Pending/Fetched jobs to DLQ in ONE SQL call!
        var count = await _storage.ArchiveCancelledBatchAsync(ids, actor ?? "Admin batch cancellation");
        _logger.LogInformation(
            ChokaQLogEvents.AdminCommandCompleted,
            "Bulk cancel: {Count}/{Total} pending jobs archived to DLQ.",
            count,
            ids.Length);
    }

    /// <summary>
    /// Batch restart: uses ResurrectBatchAsync for a single SQL batch operation.
    /// Fetcher loop will pick up newly-pending jobs automatically.
    /// </summary>
    public async Task RestartJobsAsync(IEnumerable<string> jobIds, string? actor = null)
    {
        var ids = jobIds.ToArray();
        var count = await _storage.ResurrectBatchAsync(ids, actor ?? "Admin restart");
        _logger.LogInformation(
            ChokaQLogEvents.AdminCommandCompleted,
            "Bulk restart: {Count}/{Total} jobs resurrected from DLQ.",
            count,
            ids.Length);
    }

    public override void Dispose()
    {
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }
}
