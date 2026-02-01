using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChokaQ.Core.Processing;

/// <summary>
/// The core execution engine for a single job.
/// Orchestrates the lifecycle: Fetch -> Circuit Check -> Execute -> Archive (Success/DLQ).
/// 
/// Three Pillars Integration:
/// - Success: Job archived to Archive table
/// - Failure (max retries): Job archived to DLQ
/// - Cancelled: Job archived to DLQ
/// - Retry: Job stays in Hot table with scheduled time
/// </summary>
public class JobProcessor : IJobProcessor
{
    private readonly IJobStorage _storage;
    private readonly ILogger<JobProcessor> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobDispatcher _dispatcher;
    private readonly IJobStateManager _stateManager;

    // Registry of tokens for currently running jobs to allow real-time cancellation.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    // --- Configuration ---

    /// <summary>
    /// Maximum number of retries before archiving job to DLQ.
    /// Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay (in seconds) for the exponential backoff strategy.
    /// Default: 3 seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Time to wait (in seconds) if the Circuit Breaker blocks execution.
    /// Default: 5 seconds.
    /// </summary>
    public int CircuitBreakerDelaySeconds { get; set; } = 5;

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobDispatcher dispatcher,
        IJobStateManager stateManager,
        ChokaQOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));

        if (options != null)
        {
            MaxRetries = options.MaxRetries;
            RetryDelaySeconds = options.RetryDelaySeconds;
        }
    }

    /// <summary>
    /// Thread-safe method to signal cancellation for a running job.
    /// </summary>
    public void CancelJob(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel();
        }
    }

    /// <summary>
    /// Processes a single job with full resilience logic.
    /// </summary>
    public async Task ProcessJobAsync(
        string jobId,
        string jobType,
        string payload,
        string workerId,
        int attemptCount,
        string? createdBy,
        CancellationToken workerCt)
    {
        var meta = _dispatcher.ParseMetadata(payload);
        var context = new JobExecutionContext(jobId, jobType, createdBy, meta.Queue, meta.Priority, attemptCount);

        // 1. Circuit Breaker Check
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);

            // Reschedule with delay
            await _stateManager.RescheduleForRetryAsync(
                jobId, jobType, meta.Queue, meta.Priority,
                DateTime.UtcNow.AddSeconds(CircuitBreakerDelaySeconds),
                attemptCount,
                "Circuit Breaker Open",
                workerCt);
            return;
        }

        // 2. Mark as Processing and Start Heartbeat
        await _stateManager.MarkAsProcessingAsync(
            jobId, jobType, meta.Queue, meta.Priority, attemptCount, createdBy, workerCt);

        // Create a linked token for cancellation
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(jobId, jobCts);

        // Start Heartbeat loop
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        var heartbeatTask = StartHeartbeatLoopAsync(jobId, heartbeatCts.Token);

        var sw = Stopwatch.StartNew();
        try
        {
            // 3. Dispatch execution
            await _dispatcher.DispatchAsync(jobId, jobType, payload, jobCts.Token);
            sw.Stop();

            // Stop heartbeat
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }

            // 4. SUCCESS: Archive to Archive table
            _breaker.ReportSuccess(jobType);
            await _stateManager.ArchiveSucceededAsync(
                jobId, jobType, meta.Queue, sw.Elapsed.TotalMilliseconds, workerCt);

            _logger.LogInformation(
                "[Worker {ID}] Job {JobId} succeeded in {Duration:F1}ms → Archived.",
                workerId, jobId, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            await heartbeatCts.CancelAsync();

            // CANCELLED: Archive to DLQ
            _logger.LogInformation("[Worker {ID}] Job {JobId} was cancelled.", workerId, jobId);
            await _stateManager.ArchiveCancelledAsync(
                jobId, jobType, meta.Queue, "Worker/Admin cancellation", workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await heartbeatCts.CancelAsync();

            // 5. FAILURE: Retry or Archive to DLQ
            await HandleErrorAsync(ex, context, workerId, workerCt);
        }
        finally
        {
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Periodically updates the job heartbeat in storage.
    /// Uses jitter to prevent thundering herd effect.
    /// </summary>
    private async Task StartHeartbeatLoopAsync(string jobId, CancellationToken ct)
    {
        var random = Random.Shared;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Jitter: Wait between 8 and 12 seconds
                var delayMs = random.Next(8000, 12000);
                await Task.Delay(delayMs, ct);
                await _storage.KeepAliveAsync(jobId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when job completes
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat failed for job {JobId}", jobId);
        }
    }

    private async Task HandleErrorAsync(
        Exception ex,
        JobExecutionContext context,
        string workerId,
        CancellationToken ct)
    {
        // Notify Circuit Breaker
        _breaker.ReportFailure(context.JobType);

        // Decision: Retry or Archive to DLQ?
        if (context.AttemptCount < MaxRetries)
        {
            var nextAttempt = context.AttemptCount + 1;
            var delayMs = CalculateBackoff(nextAttempt);
            var scheduledAt = DateTime.UtcNow.AddMilliseconds(delayMs);

            _logger.LogWarning(ex,
                "[Worker {WorkerId}] Job {JobId} failed. Retry #{Attempt} scheduled in {Delay}ms.",
                workerId, context.JobId, nextAttempt, delayMs);

            // RETRY: Stay in Hot table
            await _stateManager.RescheduleForRetryAsync(
                context.JobId, context.JobType, context.Queue, context.Priority,
                scheduledAt, nextAttempt, ex.Message, ct);
        }
        else
        {
            _logger.LogError(ex,
                "[Worker {WorkerId}] Job {JobId} failed permanently after {Retries} attempts → Archived to DLQ.",
                workerId, context.JobId, MaxRetries);

            // FINAL FAILURE: Archive to DLQ
            await _stateManager.ArchiveFailedAsync(
                context.JobId, context.JobType, context.Queue, ex.ToString(), ct);
        }
    }

    /// <summary>
    /// Calculates exponential backoff with jitter.
    /// Formula: (Base * 2^(attempt-1)) + Jitter
    /// </summary>
    private int CalculateBackoff(int attempt)
    {
        var baseDelay = RetryDelaySeconds * Math.Pow(2, attempt - 1);

        // Cap at 1 hour
        if (baseDelay > 3600) baseDelay = 3600;

        var jitter = Random.Shared.Next(0, 1000);
        return (int)(baseDelay * 1000) + jitter;
    }

    // Immutable context for job execution
    private record JobExecutionContext(
        string JobId,
        string JobType,
        string? CreatedBy,
        string Queue,
        int Priority,
        int AttemptCount);
}
