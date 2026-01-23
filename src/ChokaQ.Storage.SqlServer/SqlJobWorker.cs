using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// The main background service for SQL Server storage.
/// Implements a "Polling Consumer" pattern:
/// 1. Spawns multiple worker tasks (threads).
/// 2. Each worker independently polls the DB for the next available job.
/// 3. Locks the job (atomic fetch) and passes it to the Processor.
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Manages the lifecycle of individual worker threads
    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
    private readonly object _lock = new();

    // --- Configuration delegated to the Processor (Single Source of Truth) ---
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
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQL Job Worker started. Polling Interval: {Poll}ms, Empty Sleep: {Sleep}ms",
            _options.PollingInterval.TotalMilliseconds,
            _options.NoQueuesSleepInterval.TotalMilliseconds);

        // Start with 1 worker by default, or restore from saved config if we had one
        UpdateWorkerCount(1);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dynamically scales the number of active polling threads.
    /// Thread-safe.
    /// </summary>
    public void UpdateWorkerCount(int targetCount)
    {
        if (targetCount < 0) targetCount = 0;
        if (targetCount > 100) targetCount = 100; // Hard cap for safety

        lock (_lock)
        {
            int current = _workers.Count;
            if (targetCount > current)
            {
                // Scale Up
                int toAdd = targetCount - current;
                for (int i = 0; i < toAdd; i++)
                {
                    var cts = new CancellationTokenSource();
                    // We don't await the task here; it runs in the background
                    var task = Task.Run(() => WorkerLoopAsync(cts.Token), cts.Token);
                    _workers.Add((task, cts));
                }
            }
            else if (targetCount < current)
            {
                // Scale Down (Remove from the end)
                int toRemove = current - targetCount;
                for (int i = 0; i < toRemove; i++)
                {
                    var worker = _workers.Last();
                    worker.Cts.Cancel(); // Signal the loop to stop
                    _workers.RemoveAt(_workers.Count - 1);
                }
            }

            ActiveWorkers = _workers.Count;
            _logger.LogInformation("Worker count updated. Active: {Count}", ActiveWorkers);
        }
    }

    /// <summary>
    /// The main loop for a single worker thread.
    /// Continously polls the database for new jobs.
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken workerCt)
    {
        var workerId = Guid.NewGuid().ToString()[..8]; // Short ID for logs
        try
        {
            while (!workerCt.IsCancellationRequested)
            {
                bool jobProcessed = false;
                try
                {
                    // [STEP 1] Queue Discovery (Traffic Control)
                    // We only want to fetch jobs from queues that are NOT paused.
                    var allQueues = await _storage.GetQueuesAsync(workerCt);
                    var activeQueues = allQueues
                        .Where(q => !q.IsPaused)
                        .Select(q => q.Name)
                        .ToArray();

                    // [STEP 2] The Red Light Check
                    // If all queues are paused or empty, don't hammer the DB.
                    if (activeQueues.Length == 0)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("[Worker {ID}] No active queues found. Sleeping...", workerId);
                        }
                        // Sleep for a longer interval (configurable) to save resources
                        await Task.Delay(_options.NoQueuesSleepInterval, workerCt);
                        continue;
                    }

                    // [STEP 3] Fetch & Lock
                    // This calls the stored procedure (or query) with ROWLOCK/READPAST.
                    // It ensures that even with 50 workers, only ONE gets this specific job.
                    var batch = await _storage.FetchAndLockNextBatchAsync(workerId, 1, activeQueues, workerCt);
                    var jobDto = batch.FirstOrDefault();

                    if (jobDto != null)
                    {
                        jobProcessed = true;

                        // [STEP 4] Handover to Processor
                        // The worker's job is done (fetching). Now the Processor handles execution logic.
                        // We pass the raw Type string and Payload JSON.
                        await _processor.ProcessJobAsync(
                            jobDto.Id,
                            jobDto.Type,
                            jobDto.Payload,
                            workerId,
                            workerCt
                        );
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Worker {ID}] Unexpected error in polling loop.", workerId);
                    // Prevent tight loop in case of database outage
                    await Task.Delay(5000, workerCt);
                }

                // [STEP 5] Backoff Strategy
                if (!jobProcessed)
                {
                    // If no job was found, wait for the short polling interval (e.g., 1s)
                    await Task.Delay(_options.PollingInterval, workerCt);
                }
            }
        }
        finally
        {
            _logger.LogInformation("[Worker {ID}] Stopped.", workerId);
        }
    }

    // --- Management Methods (Called by Dashboard/API) ---

    public async Task CancelJobAsync(string jobId)
    {
        // 1. Try to cancel in-memory if it's currently running on THIS instance
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        _logger.LogInformation("Marking job {JobId} as Cancelled.", jobId);

        // 2. Update DB state (in case it was pending or running on another node)
        var job = await _storage.GetJobAsync(jobId);
        if (job == null) return;

        await _stateManager.UpdateStateAsync(
            jobId,
            job.Type,
            JobStatus.Cancelled,
            job.AttemptCount,
            executionDurationMs: null,
            createdBy: job.CreatedBy,
            startedAtUtc: job.StartedAtUtc,
            queue: job.Queue,
            priority: job.Priority
        );
    }

    public async Task RestartJobAsync(string jobId)
    {
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null) return;

        // Prevent restarting a job that is already running
        if (storageDto.Status == JobStatus.Processing)
        {
            _logger.LogWarning("Cannot restart job {JobId} because it is currently Processing.", jobId);
            return;
        }

        try
        {
            _logger.LogInformation("Restarting job {JobId}...", jobId);

            // Reset state to Pending and Attempts to 0
            await _stateManager.UpdateStateAsync(
                jobId,
                storageDto.Type,
                JobStatus.Pending,
                0,
                executionDurationMs: null,
                createdBy: storageDto.CreatedBy,
                startedAtUtc: null,
                queue: storageDto.Queue,
                priority: storageDto.Priority
            );

            await _storage.IncrementJobAttemptAsync(jobId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart job {JobId}.", jobId);
        }
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        // Optimistic update. The UI will reflect this, and the Poller will respect it on next fetch.
        await _storage.UpdateJobPriorityAsync(jobId, priority);
    }
}