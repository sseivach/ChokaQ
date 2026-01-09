using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Background worker implementation for SQL Server.
/// Uses a Polling mechanism to fetch pending jobs from the database.
/// Implements IWorkerManager to support Dashboard controls (Scaling, Cancellation).
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Manages the list of active worker tasks
    private readonly List<(Task Task, CancellationTokenSource Cts)> _workers = new();
    private readonly object _lock = new();

    // Polling configuration
    private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(1);

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

    public SqlJobWorker(
        IJobStorage storage,
        IJobProcessor processor,
        IJobStateManager stateManager,
        ILogger<SqlJobWorker> logger,
        SqlJobStorageOptions? options = null) // Options are optional
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new SqlJobStorageOptions();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SQL Job Worker started.");

        // Start with 1 worker by default, or configurable via options later
        UpdateWorkerCount(1);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Scales the number of concurrent polling tasks.
    /// </summary>
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
                    // Start a new independent polling loop
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
                    worker.Cts.Cancel(); // Signal cancellation
                    _workers.RemoveAt(_workers.Count - 1);
                }
            }

            ActiveWorkers = _workers.Count;
            _logger.LogInformation("Worker count updated. Active: {Count}", ActiveWorkers);
        }
    }

    /// <summary>
    /// The main polling loop for a single worker thread.
    /// Continously asks the DB for work.
    /// </summary>
    private async Task WorkerLoopAsync(CancellationToken workerCt)
    {
        var workerId = Guid.NewGuid().ToString()[..8]; // Short ID for logging

        try
        {
            while (!workerCt.IsCancellationRequested)
            {
                bool jobProcessed = false;

                try
                {
                    // 1. Fetch & Lock (Atomic operation in SQL)
                    // We fetch 1 job at a time per worker thread to ensure fair distribution
                    var batch = await _storage.FetchAndLockNextBatchAsync(workerId, 1, workerCt);
                    var jobDto = batch.FirstOrDefault();

                    if (jobDto != null)
                    {
                        jobProcessed = true;

                        // 2. Deserialize payload
                        var jobType = Type.GetType(jobDto.Type);
                        if (jobType != null)
                        {
                            var jobObject = JsonSerializer.Deserialize(jobDto.Payload, jobType) as IChokaQJob;
                            if (jobObject != null)
                            {
                                // 3. Process
                                await _processor.ProcessJobAsync(jobObject, workerId, workerCt);
                            }
                            else
                            {
                                _logger.LogError("Failed to deserialize job {JobId} payload.", jobDto.Id);

                                // UPDATE STATE: Failed (Deserialization)
                                await _stateManager.UpdateStateAsync(
                                    jobDto.Id,
                                    jobDto.Type,
                                    JobStatus.Failed,
                                    jobDto.AttemptCount,
                                    executionDurationMs: null,
                                    createdBy: jobDto.CreatedBy,
                                    startedAtUtc: null,
                                    ct: workerCt);
                            }
                        }
                        else
                        {
                            _logger.LogError("Unknown job type: {Type} for job {JobId}.", jobDto.Type, jobDto.Id);

                            // UPDATE STATE: Failed (Unknown Type)
                            await _stateManager.UpdateStateAsync(
                                jobDto.Id,
                                jobDto.Type,
                                JobStatus.Failed,
                                jobDto.AttemptCount,
                                executionDurationMs: null,
                                createdBy: jobDto.CreatedBy,
                                startedAtUtc: null,
                                ct: workerCt);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Worker {ID}] Error in polling loop.", workerId);
                    // Add a safety delay to prevent tight loop on error
                    await Task.Delay(5000, workerCt);
                }

                // 4. Backoff Strategy
                // If we didn't find any work, sleep for a while to save resources.
                // If we DID find work, try to fetch the next one immediately (High throughput).
                if (!jobProcessed)
                {
                    await Task.Delay(_pollingInterval, workerCt);
                }
            }
        }
        finally
        {
            _logger.LogInformation("[Worker {ID}] Stopped.", workerId);
        }
    }

    public async Task CancelJobAsync(string jobId)
    {
        // Try to cancel active execution via Processor
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        // Ensure state is updated in DB
        _logger.LogInformation("Marking job {JobId} as Cancelled.", jobId);

        // UPDATE STATE: Cancelled (Manual)
        await _stateManager.UpdateStateAsync(
            jobId,
            "Unknown",
            JobStatus.Cancelled,
            0,
            executionDurationMs: null,
            createdBy: null, // We don't have the DTO here, pass null
            startedAtUtc: null);
    }

    public async Task RestartJobAsync(string jobId)
    {
        // Logic similar to In-Memory, but we don't need to check "Active" status in memory explicitly
        // relying on storage state.
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto == null) return;

        // Prevent restarting active jobs
        if (storageDto.Status == JobStatus.Processing)
        {
            _logger.LogWarning("Cannot restart job {JobId} because it is currently Processing.", jobId);
            return;
        }

        try
        {
            var jobType = Type.GetType(storageDto.Type);
            if (jobType == null) throw new InvalidOperationException($"Cannot load type '{storageDto.Type}'.");

            // We don't necessarily need to deserialize the payload just to requeue it,
            // but we do need the Object to create a new "Instance" if we were using a fresh ID,
            // However, for restart, we usually just reset the status of the EXISTING record.

            _logger.LogInformation("Restarting job {JobId}...", jobId);

            // Reset state in DB to Pending, Attempt = 0
            // The Poller will pick it up automatically next time.

            // UPDATE STATE: Pending (Restart)
            await _stateManager.UpdateStateAsync(
                jobId,
                jobType.Name,
                JobStatus.Pending,
                0,
                executionDurationMs: null,
                createdBy: storageDto.CreatedBy, // Pass original creator
                startedAtUtc: null);

            await _storage.IncrementJobAttemptAsync(jobId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart job {JobId}.", jobId);
        }
    }
}