using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Concurrency;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

                // Get active queues
                var queues = await _storage.GetQueuesAsync(ct);
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
    /// </summary>
    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync(ct))
            {
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

    public override void Dispose()
    {
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }
}
