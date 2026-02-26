using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using ChokaQ.Core.Concurrency;
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
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Internal buffer: Decouples Fetching (IO Bound) from Processing (CPU Bound)
    private readonly Channel<JobHotEntity> _prefetchBuffer;

    // Encapsulates the logic for dynamic scaling (Permit Burning / Minting)
    private readonly ElasticSemaphore _concurrencyLimiter;

    // Shared cache for queue states. Updated by Fetcher, read by Processor.
    private readonly ConcurrentDictionary<string, bool> _queuePauseCache = new();

    /// <summary>
    /// Gets the number of currently executing jobs.
    /// </summary>
    public int ActiveWorkers => _concurrencyLimiter.RunningCount;

    /// <summary>
    /// Gets the maximum concurrency limit.
    /// </summary>
    public int TotalWorkers => _concurrencyLimiter.Capacity;

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

        // Bounded Channel for Backpressure
        _prefetchBuffer = Channel.CreateBounded<JobHotEntity>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });

        // Initialize elastic semaphore with default capacity of 10
        _concurrencyLimiter = new ElasticSemaphore(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SQL Worker Starting. Strategy: Prefetch + ElasticSemaphore. Initial Capacity: {Capacity}",
            _concurrencyLimiter.Capacity);

        // Two independent long-running loops:
        var fetcherTask = Task.Factory.StartNew(
            () => FetcherLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        var processorTask = Task.Factory.StartNew(
            () => ProcessorLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        await Task.WhenAll(fetcherTask, processorTask);
    }

    /// <summary>
    /// The "Supplier". Keeps the local buffer full of work from JobsHot table.
    /// Updates the local queue pause cache.
    /// </summary>
    private async Task FetcherLoopAsync(CancellationToken ct)
    {
        var workerId = $"{Environment.MachineName}-{Guid.NewGuid().ToString()[..4]}";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for space in buffer
                await _prefetchBuffer.Writer.WaitToWriteAsync(ct);

                // Determine batch size based on buffer space
                int batchSize = Math.Min(20, 100 - _prefetchBuffer.Reader.Count);
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
                var jobs = await _storage.FetchNextBatchAsync(workerId, batchSize, activeQueues, ct);

                if (!jobs.Any())
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetcher Loop crashed. Cooling down...");
                await Task.Delay(5000, ct);
            }
        }

        _logger.LogInformation("Fetcher Loop Stopped.");
    }

    /// <summary>
    /// The "Consumer". Orchestrates parallel execution limited by ElasticSemaphore.
    /// Handles immediate release of jobs if the queue becomes paused while the job is in the buffer.
    /// </summary>
    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync(ct))
            {
                // 1. Check if the queue is paused
                // If paused, we RELEASE the job back to the DB immediately.
                // We do NOT wait here, to avoid blocking the buffer for other active queues.
                if (_queuePauseCache.TryGetValue(job.Queue, out var isPaused) && isPaused)
                {
                    _logger.LogDebug("Queue {Queue} is paused. Releasing job {JobId} back to DB.", job.Queue, job.Id);

                    // Revert status to Pending, decrement attempts, clear workerId
                    await _storage.ReleaseJobAsync(job.Id, ct);

                    // Skip execution, immediately process next item in buffer
                    continue;
                }

                // 2. Wait for concurrency slot
                await _concurrencyLimiter.WaitAsync(ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Processor handles the full lifecycle including archiving
                        await _processor.ProcessJobAsync(
                            job.Id,
                            job.Type,
                            job.Payload ?? "{}",
                            "sql-worker",
                            job.AttemptCount,
                            job.CreatedBy,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Critical error in job dispatch wrapper for {JobId}.", job.Id);
                    }
                    finally
                    {
                        _concurrencyLimiter.Release();
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // --- Management Interface Implementation ---

    public void UpdateWorkerCount(int count)
    {
        _logger.LogInformation("Updating worker count to {Count}", count);
        _concurrencyLimiter.SetCapacity(count);
    }

    public async Task CancelJobAsync(string jobId)
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
            await _stateManager.ArchiveCancelledAsync(jobId, job.Type, job.Queue, "Admin cancellation");
        }
    }

    public async Task RestartJobAsync(string jobId)
    {
        // Check Hot table first
        var hotJob = await _storage.GetJobAsync(jobId);
        if (hotJob != null && hotJob.Status == JobStatus.Processing)
        {
            _logger.LogWarning("Cannot restart job {JobId}: still processing.", jobId);
            return;
        }

        // Check DLQ for resurrection
        var dlqJob = await _storage.GetDLQJobAsync(jobId);
        if (dlqJob != null)
        {
            _logger.LogInformation("Resurrecting job {JobId} from DLQ...", jobId);
            await _storage.ResurrectAsync(jobId, null, "Admin restart");
            return;
        }

        _logger.LogWarning("Cannot restart job {JobId}: not found in DLQ.", jobId);
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

    /// <summary>
    /// Batch cancellation via parallel Task.WhenAll.
    /// </summary>
    /// <summary>
    /// Batch cancellation: stops running tasks in memory, then uses a single DB roundtrip 
    /// (via OPENJSON) to move all remaining Pending/Fetched jobs to the DLQ.
    /// </summary>
    public async Task CancelJobsAsync(IEnumerable<string> jobIds)
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
        var count = await _storage.ArchiveCancelledBatchAsync(ids, "Admin batch cancellation");
        _logger.LogInformation("Bulk cancel: {Count}/{Total} pending jobs archived to DLQ.", count, ids.Length);
    }

    /// <summary>
    /// Batch restart: uses ResurrectBatchAsync for a single SQL batch operation.
    /// Fetcher loop will pick up newly-pending jobs automatically.
    /// </summary>
    public async Task RestartJobsAsync(IEnumerable<string> jobIds)
    {
        var ids = jobIds.ToArray();
        var count = await _storage.ResurrectBatchAsync(ids, "Admin restart");
        _logger.LogInformation("Bulk restart: {Count}/{Total} jobs resurrected from DLQ.", count, ids.Length);
    }

    public override void Dispose()
    {
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }
}
