using ChokaQ.Abstractions.Observability;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Exceptions;
using ChokaQ.Core.Execution;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ChokaQ.Core.Processing;

/// <summary>
/// The core execution engine for a single job.
/// Orchestrates the entire lifecycle: Fetch -> Circuit Check -> Execute -> Archive (Success/DLQ).
/// Includes Smart Worker capabilities to fast-fail on non-transient errors.
/// </summary>
public class JobProcessor : IJobProcessor
{
    private readonly IJobStorage _storage;
    private readonly ILogger<JobProcessor> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobDispatcher _dispatcher;
    private readonly IJobStateManager _stateManager;
    private readonly IChokaQMetrics _metrics;

    // Registry of tokens for currently running jobs to allow real-time cancellation.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 3;
    public int CircuitBreakerDelaySeconds { get; set; } = 5;

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobDispatcher dispatcher,
        IJobStateManager stateManager,
        IChokaQMetrics metrics,
        ChokaQOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        if (options != null)
        {
            MaxRetries = options.MaxRetries;
            RetryDelaySeconds = options.RetryDelaySeconds;
        }
    }

    /// <summary>
    /// Triggers immediate cancellation for a specific running job.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel.</param>
    public void CancelJob(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation("Requesting cancellation for running job {JobId}...", jobId);
            cts.Cancel();
        }
    }

    /// <summary>
    /// Processes a single job fetched from the storage.
    /// Manages timeouts, cancellations, circuit breakers, and state transitions.
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

        // 1. Circuit Breaker Check: Prevent cascading failures if external service is down
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);

            await _stateManager.RescheduleForRetryAsync(
                jobId, jobType, meta.Queue, meta.Priority,
                DateTime.UtcNow.AddSeconds(CircuitBreakerDelaySeconds),
                attemptCount,
                "Circuit Breaker Open",
                workerCt);
            return;
        }

        // 2. Mark as Processing and Start Heartbeat (Zombie prevention)
        await _stateManager.MarkAsProcessingAsync(
            jobId, jobType, meta.Queue, meta.Priority, attemptCount, createdBy, workerCt);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(jobId, jobCts);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        var heartbeatTask = StartHeartbeatLoopAsync(jobId, heartbeatCts.Token);

        var sw = Stopwatch.StartNew();
        try
        {
            // 3. Dispatch execution to the actual handler via compiled expression trees
            await _dispatcher.DispatchAsync(jobId, jobType, payload, jobCts.Token);
            sw.Stop();

            // Stop the heartbeat before finalizing
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }

            // 4. SUCCESS: Report to metrics and Circuit Breaker, move to Archive
            _breaker.ReportSuccess(jobType);
            _metrics.RecordSuccess(meta.Queue, jobType, sw.Elapsed.TotalMilliseconds);

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

            _logger.LogInformation("[Worker {ID}] Job {JobId} was cancelled.", workerId, jobId);

            // Move to Dead Letter Queue (Morgue) on explicit cancellation
            await _stateManager.ArchiveCancelledAsync(
                jobId, jobType, meta.Queue, "Worker/Admin cancellation", workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await heartbeatCts.CancelAsync();

            _metrics.RecordFailure(meta.Queue, jobType, ex.GetType().Name);

            // 5. FAILURE HANDLING: Determine if we should retry or fast-fail
            await HandleErrorAsync(ex, context, workerId, workerCt);
        }
        finally
        {
            // Cleanup cancellation token
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    /// <summary>
    /// Continuously updates the job's heartbeat timestamp in storage to prevent it from being marked as a Zombie.
    /// </summary>
    private async Task StartHeartbeatLoopAsync(string jobId, CancellationToken ct)
    {
        var random = Random.Shared;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Add jitter to avoid DB thundering herd problem
                var delayMs = random.Next(8000, 12000);
                await Task.Delay(delayMs, ct);
                await _storage.KeepAliveAsync(jobId, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat failed for job {JobId}", jobId);
        }
    }

    /// <summary>
    /// Handles job execution failures. Evaluates transient vs non-transient errors.
    /// </summary>
    private async Task HandleErrorAsync(
        Exception ex,
        JobExecutionContext context,
        string workerId,
        CancellationToken ct)
    {
        // Notify Circuit Breaker about the failure
        _breaker.ReportFailure(context.JobType);

        // --- SMART WORKER LOGIC: FAST FAIL FOR NON-TRANSIENT ERRORS ---
        if (IsFatalException(ex))
        {
            _logger.LogError(ex,
                "[Worker {WorkerId}] Job {JobId} failed with FATAL error. Bypassing retries. Attempt: {Attempt} -> Archived to DLQ.",
                workerId, context.JobId, context.AttemptCount);

            // Send directly to Morgue (DLQ) with current AttemptCount to indicate early death
            await _stateManager.ArchiveFailedAsync(
                context.JobId, context.JobType, context.Queue, $"FATAL: {ex.Message}\n{ex.StackTrace}", ct);
            return;
        }

        // --- STANDARD EXPONENTIAL BACKOFF RETRY LOGIC ---
        if (context.AttemptCount < MaxRetries)
        {
            var nextAttempt = context.AttemptCount + 1;
            var delayMs = CalculateBackoff(nextAttempt);
            var scheduledAt = DateTime.UtcNow.AddMilliseconds(delayMs);

            _logger.LogWarning(ex,
                "[Worker {WorkerId}] Job {JobId} failed. Retry #{Attempt} scheduled in {Delay}ms.",
                workerId, context.JobId, nextAttempt, delayMs);

            // RETRY: Keep job in Hot table but delay its visibility
            await _stateManager.RescheduleForRetryAsync(
                context.JobId, context.JobType, context.Queue, context.Priority,
                scheduledAt, nextAttempt, ex.Message, ct);
        }
        else
        {
            _logger.LogError(ex,
                "[Worker {WorkerId}] Job {JobId} failed permanently after {Retries} attempts → Archived to DLQ.",
                workerId, context.JobId, MaxRetries);

            // FINAL FAILURE: Exhausted all retries, move to Dead Letter Queue (Morgue)
            await _stateManager.ArchiveFailedAsync(
                context.JobId, context.JobType, context.Queue, ex.ToString(), ct);
        }
    }

    /// <summary>
    /// Determines if an exception is non-transient (fatal) and should bypass retry policies.
    /// </summary>
    private static bool IsFatalException(Exception ex)
    {
        // Unwrap TargetInvocationException or AggregateException
        var root = ex.GetBaseException();

        return root is ChokaQFatalException ||
               root is ArgumentException ||
               root is NullReferenceException ||
               root is InvalidOperationException ||
               root is NotSupportedException ||
               root is JsonException ||
               root is FormatException;
    }

    /// <summary>
    /// Calculates exponential backoff delay with jitter to prevent retry storms.
    /// </summary>
    private int CalculateBackoff(int attempt)
    {
        var baseDelay = RetryDelaySeconds * Math.Pow(2, attempt - 1);
        if (baseDelay > 3600) baseDelay = 3600; // Cap at 1 hour

        // Add jitter to spread out retries
        var jitter = Random.Shared.Next(0, 1000);
        return (int)(baseDelay * 1000) + jitter;
    }

    private record JobExecutionContext(
        string JobId,
        string JobType,
        string? CreatedBy,
        string Queue,
        int Priority,
        int AttemptCount);
}