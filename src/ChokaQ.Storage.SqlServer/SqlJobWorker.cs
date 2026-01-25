using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Concurrency;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Worker implementing the "Prefetching Consumer" pattern.
/// Decouples DB fetching latency from Job Execution throughput using an elastic concurrency limiter.
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Internal buffer: Decouples Fetching (IO Bound) from Processing (CPU Bound).
    // Acts as a "Leaky Bucket" for load leveling.
    private readonly Channel<JobStorageDto> _prefetchBuffer;

    // Encapsulates the logic for dynamic scaling (Permit Burning / Minting).
    private readonly ElasticSemaphore _concurrencyLimiter;

    /// <summary>
    /// Gets the number of currently executing jobs.
    /// </summary>
    public int ActiveWorkers => _concurrencyLimiter.RunningCount;

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

        // Use a Bounded Channel to apply Backpressure. 
        // If execution is slow, the buffer fills up (limit 100), 
        // and the Fetcher automatically waits before pulling more from DB.
        _prefetchBuffer = Channel.CreateBounded<JobStorageDto>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true, // Only the Fetcher writes
            SingleReader = false // Multiple virtual consumers read
        });

        // Initialize the elastic semaphore with a default capacity of 10.
        // It uses int.MaxValue internally to allow infinite scaling UP.
        _concurrencyLimiter = new ElasticSemaphore(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "🚀 Worker Starting. Strategy: Prefetch + ElasticSemaphore. Initial Capacity: {Capacity}",
            _concurrencyLimiter.Capacity);

        // Spawning TWO independent long-running loops:

        // 1. The Supplier (Fetch from DB -> Memory)
        var fetcherTask = Task.Factory.StartNew(
            () => FetcherLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        // 2. The Consumer (Memory -> CPU execution)
        var processorTask = Task.Factory.StartNew(
            () => ProcessorLoopAsync(stoppingToken),
            TaskCreationOptions.LongRunning);

        await Task.WhenAll(fetcherTask, processorTask);
    }

    /// <summary>
    /// The "Supplier". Its only job is to keep the local buffer full of work.
    /// Uses Batch Fetching to reduce DB roundtrips.
    /// </summary>
    private async Task FetcherLoopAsync(CancellationToken ct)
    {
        var workerId = Environment.MachineName; // Identify node for locking

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // [Optimization] Flow Control
                // Only fetch if we have space in the buffer.
                // Channel.WriteAsync will block if full, providing natural backpressure.
                await _prefetchBuffer.Writer.WaitToWriteAsync(ct);

                // Determine how many slots are free (approx).
                // We fetch slightly more than needed to keep the pipe full, but not too much.
                int batchSize = Math.Min(20, 100 - _prefetchBuffer.Reader.Count);

                if (batchSize <= 0)
                {
                    batchSize = 1;
                }

                // [Optimization] Active Queue Discovery
                var queues = await _storage.GetQueuesAsync(ct);
                var activeQueues = queues.Where(q => !q.IsPaused).Select(q => q.Name).ToArray();

                if (activeQueues.Length == 0)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("No active queues. Fetcher sleeping...");
                    }

                    await Task.Delay(_options.NoQueuesSleepInterval, ct);
                    continue;
                }

                // Atomic Fetch & Lock from DB
                // This is the only time we touch the DB in this loop.
                var jobs = await _storage.FetchAndLockNextBatchAsync(workerId, batchSize, activeQueues, ct);

                if (!jobs.Any())
                {
                    // Exponential backoff or static sleep on empty DB to save resources.
                    await Task.Delay(_options.PollingInterval, ct);
                    continue;
                }

                foreach (var job in jobs)
                {
                    // Push to memory buffer. 
                    // If buffer is full, this line AWAITS until a processor frees up space.
                    // This is the Magic of Backpressure.
                    await _prefetchBuffer.Writer.WriteAsync(job, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 Fetcher Loop crashed. Cooling down...");
                await Task.Delay(5000, ct);
            }
        }

        _logger.LogInformation("Fetcher Loop Stopped.");
    }

    /// <summary>
    /// The "Consumer". Orchestrates parallel execution limited by the ElasticSemaphore.
    /// Does NOT poll the DB. Only eats from the Channel.
    /// </summary>
    /// <summary>
    /// The "Consumer". Orchestrates parallel execution limited by the ElasticSemaphore.
    /// Does NOT poll the DB. Only eats from the Channel.
    /// </summary>
    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync(ct))
            {
                // Ждем слот
                await _concurrencyLimiter.WaitAsync(ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 1. PING DB: "I am really starting now!"
                        // Status: Fetched (1) -> Processing (2)
                        await _storage.MarkAsProcessingAsync(job.Id, ct);

                        // 2. Process
                        await _processor.ProcessJobAsync(
                            job.Id,
                            job.Type,
                            job.Payload,
                            "prefetch-worker",
                            job.AttemptCount,
                            job.CreatedBy,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Critical error in job dispatch wrapper.");
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
        // This is where the magic happens.
        // We simply delegate the complexity to our ElasticSemaphore wrapper.
        // It handles scaling UP (releasing) and scaling DOWN (burning permits).
        _logger.LogInformation("Updating worker count to {Count}", count);

        _concurrencyLimiter.SetCapacity(count);
    }

    public async Task CancelJobAsync(string jobId)
    {
        // Proxy to Processor to cancel running cancellation token
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        // Also ensure state is updated in DB
        var job = await _storage.GetJobAsync(jobId);

        if (job != null)
        {
            await _stateManager.UpdateStateAsync(
                jobId,
                job.Type,
                JobStatus.Cancelled,
                job.AttemptCount,
                createdBy: job.CreatedBy,
                startedAtUtc: job.StartedAtUtc,
                queue: job.Queue,
                priority: job.Priority);
        }
    }

    public async Task RestartJobAsync(string jobId)
    {
        var job = await _storage.GetJobAsync(jobId);

        if (job == null || job.Status == JobStatus.Processing)
        {
            return;
        }

        // Reset to pending
        await _stateManager.UpdateStateAsync(
             jobId,
             job.Type,
             JobStatus.Pending,
             0, // Reset attempts
             createdBy: job.CreatedBy,
             startedAtUtc: null,
             queue: job.Queue,
             priority: job.Priority);

        await _storage.IncrementJobAttemptAsync(jobId, 0);
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        await _storage.UpdateJobPriorityAsync(jobId, priority);
    }

    public override void Dispose()
    {
        // Clean up managed resources
        _concurrencyLimiter.Dispose();
        base.Dispose();
    }
}