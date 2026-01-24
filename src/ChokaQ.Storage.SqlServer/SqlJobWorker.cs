using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Processing;
using ChokaQ.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace ChokaQ.Storage.SqlServer;

/// <summary>
/// Worker implementing the "Prefetching Consumer" pattern.
/// Decouples DB fetching latency from Job Execution throughput.
/// </summary>
public class SqlJobWorker : BackgroundService, IWorkerManager
{
    private readonly IJobStorage _storage;
    private readonly IJobProcessor _processor;
    private readonly IJobStateManager _stateManager;
    private readonly ILogger<SqlJobWorker> _logger;
    private readonly SqlJobStorageOptions _options;

    // Internal buffer: Decouples Fetching (IO Bound) from Processing (CPU Bound)
    // Acts as a "Leaky Bucket" for load leveling.
    private readonly Channel<JobStorageDto> _prefetchBuffer;

    // Limits concurrency using a Semaphore instead of thread spamming.
    // Much lighter on memory than creating new Task/Thread per job.
    private readonly SemaphoreSlim _concurrencyLimiter;
    private int _targetConcurrency = 10;
    private readonly object _lock = new();

    public int ActiveWorkers => _targetConcurrency - _concurrencyLimiter.CurrentCount;

    // Delegate configuration to the central Processor
    public int MaxRetries {
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

        _concurrencyLimiter = new SemaphoreSlim(_targetConcurrency, _targetConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Worker Starting. Strategy: Prefetch + Semaphore.");

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

                // Determine how many slots are free (approx)
                // We fetch slightly more than needed to keep the pipe full, but not too much.
                int batchSize = Math.Min(20, 100 - _prefetchBuffer.Reader.Count);
                if (batchSize <= 0) batchSize = 1;

                // [Optimization] Active Queue Discovery
                var queues = await _storage.GetQueuesAsync(ct);
                var activeQueues = queues.Where(q => !q.IsPaused).Select(q => q.Name).ToArray();

                if (activeQueues.Length == 0)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("No active queues. Fetcher sleeping...");

                    await Task.Delay(_options.NoQueuesSleepInterval, ct);
                    continue;
                }

                // Atomic Fetch & Lock from DB
                // This is the only time we touch the DB in this loop
                var jobs = await _storage.FetchAndLockNextBatchAsync(workerId, batchSize, activeQueues, ct);

                if (!jobs.Any())
                {
                    // Exponential backoff or static sleep on empty DB to save resources
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
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "🔥 Fetcher Loop crashed. Cooling down...");
                await Task.Delay(5000, ct);
            }
        }
        _logger.LogInformation("Fetcher Loop Stopped.");
    }

    /// <summary>
    /// The "Consumer". Orchestrates parallel execution limited by Semaphore.
    /// Does NOT poll the DB. Only eats from the Channel.
    /// </summary>
    private async Task ProcessorLoopAsync(CancellationToken ct)
    {
        try
        {
            // Continuously read from the in-memory buffer
            await foreach (var job in _prefetchBuffer.Reader.ReadAllAsync(ct))
            {
                // Wait for a concurrency slot (Throttling)
                // If we are at max capacity (10), this waits until someone finishes.
                await _concurrencyLimiter.WaitAsync(ct);

                // Fire and forget (but tracked via Semaphore)
                // We don't await the job itself here, otherwise we'd be serial.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Handover to Business Logic
                        await _processor.ProcessJobAsync(
                            job.Id,
                            job.Type,
                            job.Payload,
                            "prefetch-worker",
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Critical error in job dispatch wrapper.");
                    }
                    finally
                    {
                        // Always release the slot so the next job can start
                        _concurrencyLimiter.Release();
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _logger.LogInformation("Processor Loop Stopped.");
        }
    }

    // --- Management Interface Implementation ---

    public void UpdateWorkerCount(int count)
    {
        // In this architecture, scaling means resizing the Semaphore.
        // This is much cheaper than destroying/creating Threads.
        if (count <= 0) count = 1;
        if (count > 100) count = 100;

        lock (_lock)
        {
            int diff = count - _targetConcurrency;

            if (diff > 0)
            {
                // Scaling UP: Just give more permits
                _concurrencyLimiter.Release(diff);
            }
            else if (diff < 0)
            {
                // Scaling DOWN is tricky with SemaphoreSlim (it doesn't have "Reduce").
                // In a real production code, we would recreate the semaphore or track "debt".
                // For simplicity here, we just accept the new target and it will apply on restart 
                // or we implement a debt counter. 
                // Let's just log it for now as "Soft Limit" update.
                // To do it properly requires a more complex custom Semaphore wrapper.
                _logger.LogWarning("Scaling down via Semaphore is delayed until restart in this version.");
            }

            _targetConcurrency = count;
            _logger.LogInformation("🎯 Target Concurrency adjusted to {Count}", _targetConcurrency);
        }
    }

    public async Task CancelJobAsync(string jobId)
    {
        // Proxy to Processor to cancel running token
        if (_processor is JobProcessor concreteProcessor)
        {
            concreteProcessor.CancelJob(jobId);
        }

        // Also ensure state is updated in DB
        var job = await _storage.GetJobAsync(jobId);
        if (job != null)
        {
            await _stateManager.UpdateStateAsync(
                jobId, job.Type, JobStatus.Cancelled, job.AttemptCount,
                createdBy: job.CreatedBy, startedAtUtc: job.StartedAtUtc, queue: job.Queue, priority: job.Priority);
        }
    }

    public async Task RestartJobAsync(string jobId)
    {
        var job = await _storage.GetJobAsync(jobId);
        if (job == null || job.Status == JobStatus.Processing) return;

        // Reset to pending
        await _stateManager.UpdateStateAsync(
             jobId, job.Type, JobStatus.Pending, 0, // Reset attempts
             createdBy: job.CreatedBy, startedAtUtc: null, queue: job.Queue, priority: job.Priority);

        await _storage.IncrementJobAttemptAsync(jobId, 0);
    }

    public async Task SetJobPriorityAsync(string jobId, int priority)
    {
        await _storage.UpdateJobPriorityAsync(jobId, priority);
    }
}