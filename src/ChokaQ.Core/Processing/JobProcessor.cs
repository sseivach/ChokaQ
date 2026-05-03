using ChokaQ.Abstractions.Observability;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Exceptions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
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
    private readonly TimeProvider _timeProvider;
    private readonly ChokaQOptions _options;

    // Registry of tokens for currently running jobs to allow real-time cancellation.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    public int MaxRetries
    {
        get => _options.Retry.MaxAttempts;
        set => _options.Retry.MaxAttempts = value;
    }

    public int RetryDelaySeconds
    {
        get => (int)_options.Retry.BaseDelay.TotalSeconds;
        set => _options.Retry.BaseDelay = TimeSpan.FromSeconds(value);
    }

    public int CircuitBreakerDelaySeconds
    {
        get => (int)_options.Retry.CircuitBreakerDelay.TotalSeconds;
        set => _options.Retry.CircuitBreakerDelay = TimeSpan.FromSeconds(value);
    }

    public JobProcessor(
        IJobStorage storage,
        ILogger<JobProcessor> logger,
        ICircuitBreaker breaker,
        IJobDispatcher dispatcher,
        IJobStateManager stateManager,
        IChokaQMetrics metrics,
        ChokaQOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new ChokaQOptions();
        _options.ValidateOrThrow();
    }

    /// <summary>
    /// Triggers immediate cancellation for a specific running job.
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel.</param>
    public void CancelJob(string jobId)
    {
        if (_activeJobTokens.TryGetValue(jobId, out var cts))
        {
            _logger.LogInformation(
                ChokaQLogEvents.JobCancellationRequested,
                "Requesting cancellation for running job {JobId}.",
                jobId);
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
        DateTime? scheduledAtUtc,
        DateTime createdAtUtc,
        CancellationToken workerCt)
    {
        var meta = _dispatcher.ParseMetadata(payload);

        // ==============================================================================================
        // PHASE 1: OBSERVABILITY & DECISION-DRIVEN TELEMETRY
        // ==============================================================================================
        // In high-performance systems, "Queue Lag" (time-in-queue) is the primary signal of system saturation.
        // It provides a much more accurate representation of cluster health than simple queue depth.
        // If lag spikes, the system is under-provisioned or blocked, and autoscaling should trigger.
        var lagMs = (_timeProvider.GetUtcNow().UtcDateTime - (scheduledAtUtc ?? createdAtUtc)).TotalMilliseconds;
        _metrics.RecordQueueLag(meta.Queue, jobType, Math.Max(0, lagMs));

        // Tracking active workers provides a real-time gauge of concurrent system load.
        _metrics.RecordActiveWorkerDelta(meta.Queue, 1);
        try
        {
            // attemptCount is the persisted count of executions that already crossed the
            // Fetched -> Processing boundary. Fetching alone does not count as an attempt;
            // otherwise pause/shutdown/recovery churn would silently burn retry budget.
            var storedAttemptCount = attemptCount;

        // 1. Circuit Breaker Check: Prevent cascading failures if external service is down
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning(
                ChokaQLogEvents.CircuitBreakerRejected,
                "[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.",
                jobId,
                jobType);

            await _stateManager.RescheduleForRetryAsync(
                jobId, jobType, meta.Queue, meta.Priority,
                _timeProvider.GetUtcNow().UtcDateTime.Add(_options.Retry.CircuitBreakerDelay),
                storedAttemptCount,
                "Circuit Breaker Open",
                workerCt,
                workerId);
            return;
        }

        var executionAttempt = storedAttemptCount + 1;

        // 2. Mark as Processing and Start Heartbeat (Zombie prevention)
        // ----------------------------------------------------------------------------------
        // CRITICAL (LEASE GATE): SQL workers prefetch jobs into an in-memory buffer. Between
        // fetch and actual execution, that persisted lease may be released because the queue
        // was paused, the host is shutting down, or abandoned-fetch recovery reclaimed it.
        // MarkAsProcessing is therefore the final ownership check before user code runs.
        // If it returns false, this worker is holding a stale copy and must not dispatch it.
        // ----------------------------------------------------------------------------------
        var executionLeaseAcquired = await _stateManager.MarkAsProcessingAsync(
            jobId, jobType, meta.Queue, meta.Priority, executionAttempt, createdBy, workerCt, workerId);
        if (!executionLeaseAcquired)
        {
            _logger.LogWarning(
                ChokaQLogEvents.JobExecutionLeaseRejected,
                "[Worker {ID}] Job {JobId} was not started because the execution lease is no longer valid.",
                workerId, jobId);
            return;
        }

        var context = new JobExecutionContext(jobId, jobType, createdBy, meta.Queue, meta.Priority, executionAttempt);

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        var executionTimeout = _options.GetExecutionTimeoutForQueue(meta.Queue);

        // Every background processor needs a hard execution boundary. Without it, a handler
        // that hangs on I/O can hold a worker lease forever, hide saturation, and force humans
        // to repair the system manually. Per-queue overrides let long-running workloads be
        // explicit instead of weakening the default timeout for the whole installation.
        jobCts.CancelAfter(executionTimeout);
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
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }

            // 4. SUCCESS: Report to metrics and Circuit Breaker, move to Archive
            // ----------------------------------------------------------------------------------
            // CRITICAL (EXECUTION BOUNDARY): ChokaQ guarantees AT-LEAST-ONCE delivery.
            // If the process crashes after DispatchAsync but before ArchiveSucceededAsync completes,
            // the job will be picked up again by the zombie reclaimer and retried.
            // 👉 Handlers MUST be implemented idempotently (e.g., using idempotency keys).
            // ----------------------------------------------------------------------------------
            _breaker.ReportSuccess(jobType);
            _metrics.RecordSuccess(meta.Queue, jobType, sw.Elapsed.TotalMilliseconds);

            await _stateManager.ArchiveSucceededAsync(
                jobId, jobType, meta.Queue, sw.Elapsed.TotalMilliseconds, workerCt, workerId);

            _logger.LogInformation(
                ChokaQLogEvents.JobSucceededArchived,
                "[Worker {ID}] Job {JobId} succeeded in {Duration:F1}ms → Archived.",
                workerId, jobId, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (workerCt.IsCancellationRequested)
        {
            sw.Stop();
            heartbeatCts.Cancel();

            _logger.LogInformation(
                ChokaQLogEvents.JobShutdownRescheduled,
                "[Worker {ID}] Job {JobId} was cancelled due to worker shutdown.",
                workerId,
                jobId);

            // Shutdown: Reschedule immediately so it isn't orphaned
            await _stateManager.RescheduleForRetryAsync(
                jobId, jobType, meta.Queue, meta.Priority, _timeProvider.GetUtcNow().UtcDateTime, context.AttemptCount, "Worker Shutdown", CancellationToken.None, workerId);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            heartbeatCts.Cancel();

            _logger.LogWarning(
                ChokaQLogEvents.JobTimedOutOrCancelled,
                "[Worker {ID}] Job {JobId} timed out or was explicitly cancelled by admin.",
                workerId,
                jobId);

            // Timeout or Admin Cancel -> DLQ
            await _stateManager.ArchiveCancelledAsync(
                jobId, jobType, meta.Queue, ChokaQ.Abstractions.Enums.JobCancellationReason.Timeout, "Execution Timeout or Admin Cancellation", workerCt, workerId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            heartbeatCts.Cancel();

            _metrics.RecordFailure(meta.Queue, jobType, ex.GetType().Name);

            // 5. FAILURE HANDLING: Determine if we should retry or fast-fail
            await HandleErrorAsync(ex, context, workerId, workerCt);
        }
        finally
        {
            // Always remove and dispose the per-job CTS. A long-lived worker processes many
            // jobs; leaking even small token registrations would become a slow operational bug.
            if (_activeJobTokens.TryRemove(jobId, out var cts))
            {
                cts.Dispose();
            }
        }
        }
        finally
        {
            _metrics.RecordActiveWorkerDelta(meta.Queue, -1);
        }
    }

    /// <summary>
    /// Continuously updates the job's heartbeat timestamp in storage to prevent it from being marked as a Zombie.
    /// </summary>
    private async Task StartHeartbeatLoopAsync(string jobId, CancellationToken ct)
    {
        int consecutiveFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Add jitter to avoid DB thundering herd problem
                var delay = GetRandomDelay(_options.Execution.HeartbeatIntervalMin, _options.Execution.HeartbeatIntervalMax);
                await Task.Delay(delay, ct);
                
                try
                {
                    await _storage.KeepAliveAsync(jobId, ct);
                    consecutiveFailures = 0; // reset on success
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    consecutiveFailures++;
                    _logger.LogWarning(
                        ChokaQLogEvents.JobHeartbeatWriteFailed,
                        ex,
                        "Heartbeat failed for job {JobId}. Consecutive failures: {Count}",
                        jobId,
                        consecutiveFailures);
                    
                    if (consecutiveFailures >= _options.Execution.HeartbeatFailureThreshold)
                    {
                        _logger.LogError(
                            ChokaQLogEvents.JobHeartbeatThresholdReached,
                            "Heartbeat failed {Threshold} times for job {JobId}. Cancelling job execution.",
                            _options.Execution.HeartbeatFailureThreshold,
                            jobId);
                        CancelJob(jobId);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Handles job execution failures by enforcing a strict Failure Taxonomy.
    /// Determines whether an error is Transient (retryable), Fatal (dead-letter immediately), 
    /// or Throttled (respect exact delay to prevent retry storms).
    /// </summary>
    private async Task HandleErrorAsync(
        Exception ex,
        JobExecutionContext context,
        string workerId,
        CancellationToken ct)
    {
        // ==============================================================================================
        // PHASE 2: FAILURE TAXONOMY & POISON PILL PROTECTION
        // ==============================================================================================
        // Not all errors are equal. Treating all exceptions as transient leads to degraded throughput
        // because "Poison Pills" (e.g. malformed JSON, missing dependencies) will repeatedly fail
        // and waste valuable worker cycles. We use a strict taxonomy to bypass retries when appropriate.

        bool isFatal = IsFatalException(ex);

        // Notify Circuit Breaker about the failure with severity
        _breaker.ReportFailure(context.JobType, isFatal ? ChokaQ.Abstractions.Enums.CircuitFailureSeverity.Fatal : ChokaQ.Abstractions.Enums.CircuitFailureSeverity.Transient);

        // --- SMART WORKER LOGIC: FAST FAIL FOR NON-TRANSIENT ERRORS ---
        if (isFatal)
        {
            // By short-circuiting the retry loop and moving directly to the Dead Letter Queue (DLQ), 
            // we prevent "Poison Pills" from hogging worker leases.
            _logger.LogError(
                ChokaQLogEvents.JobFatalErrorDlq,
                ex,
                "[Worker {WorkerId}] Job {JobId} failed with FATAL error. Bypassing retries. Attempt: {Attempt} -> Archived to DLQ.",
                workerId, context.JobId, context.AttemptCount);

            _metrics.RecordDlq(context.Queue, context.JobType, "Fatal Error");

            // Send directly to Morgue (DLQ) with current AttemptCount to indicate early death
            await _stateManager.ArchiveFailedAsync(
                context.JobId,
                context.JobType,
                context.Queue,
                $"FATAL: {ex.Message}\n{ex.StackTrace}",
                ct,
                workerId,
                FailureReason.FatalError);
            return;
        }

        // --- STANDARD EXPONENTIAL BACKOFF RETRY LOGIC ---
        if (context.AttemptCount < MaxRetries)
        {
            var nextAttempt = context.AttemptCount + 1;
            int delayMs;

            if (ex is ChokaQ.Abstractions.Exceptions.IChokaQThrottledException throttledEx && throttledEx.RetryAfter.HasValue)
            {
                // THROTTLED (HTTP 429): Respect the downstream service's request to back off,
                // bounded by Retry.MaxDelay so one dependency cannot schedule unbounded local delay.
                // Ignoring Retry-After headers can lead to unintentional DDoS of external dependencies.
                delayMs = ClampDelayToSchedulerRange(throttledEx.RetryAfter.Value);
                _logger.LogWarning(
                    ChokaQLogEvents.JobThrottledRetryScheduled,
                    ex,
                    "[Worker {WorkerId}] Job {JobId} THROTTLED. Retry-After delay applied. Retry #{Attempt} scheduled in {Delay}ms.",
                    workerId, context.JobId, nextAttempt, delayMs);
            }
            else
            {
                delayMs = CalculateBackoff(nextAttempt);
                _logger.LogWarning(
                    ChokaQLogEvents.JobTransientRetryScheduled,
                    ex,
                    "[Worker {WorkerId}] Job {JobId} failed with TRANSIENT error. Retry #{Attempt} scheduled in {Delay}ms.",
                    workerId, context.JobId, nextAttempt, delayMs);
            }

            var scheduledAt = _timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(delayMs);

            _metrics.RecordRetry(context.Queue, context.JobType, nextAttempt);

            // RETRY: Keep job in Hot table but delay its visibility. We persist the number
            // of attempts already executed, not the next attempt number. The next increment
            // happens only when the job crosses MarkAsProcessing again.
            await _stateManager.RescheduleForRetryAsync(
                context.JobId, context.JobType, context.Queue, context.Priority,
                scheduledAt, context.AttemptCount, ex.Message, ct, workerId);
        }
        else
        {
            var failureReason = IsThrottledException(ex)
                ? FailureReason.Throttled
                : FailureReason.Transient;

            _logger.LogError(
                ChokaQLogEvents.JobRetriesExhaustedDlq,
                ex,
                "[Worker {WorkerId}] Job {JobId} failed permanently after {Retries} attempts → Archived to DLQ.",
                workerId, context.JobId, MaxRetries);

            _metrics.RecordDlq(context.Queue, context.JobType, failureReason.ToString());

            // FINAL FAILURE: Exhausted all retries, move to Dead Letter Queue (Morgue).
            // The taxonomy captures why retries ended. Operators should respond very differently
            // to throttling (reduce concurrency / honor quotas) than to generic transient failure
            // (inspect downstream availability, retry budget, and handler resilience).
            await _stateManager.ArchiveFailedAsync(
                context.JobId,
                context.JobType,
                context.Queue,
                ex.ToString(),
                ct,
                workerId,
                failureReason);
        }
    }

    /// <summary>
    /// Determines if an exception is non-transient (fatal) and should bypass retry policies.
    /// </summary>
    private static bool IsFatalException(Exception ex)
    {
        // Unwrap TargetInvocationException or AggregateException
        var root = ex.GetBaseException();

        // Support for policy-based marker interfaces from user code
        if (root.GetType().GetInterfaces().Any(i => i.Name == "IChokaQFatalException"))
        {
            return true;
        }

        return root is ChokaQFatalException ||
               root is ArgumentException ||
               root is InvalidOperationException ||
               root is NotSupportedException ||
               root is JsonException ||
               root is FormatException;
    }

    private static bool IsThrottledException(Exception ex)
    {
        var root = ex.GetBaseException();
        return root is IChokaQThrottledException ||
               root.GetType().GetInterfaces().Any(i => i.Name == nameof(IChokaQThrottledException));
    }

    /// <summary>
    /// Calculates exponential backoff delay to prevent Retry Storms.
    /// </summary>
    private int CalculateBackoff(int attempt)
    {
        // ==============================================================================================
        // PHASE 2: RETRY STORM AWARENESS & MITIGATION
        // ==============================================================================================
        // A naive retry strategy (e.g. fixed 5 seconds) causes the "Thundering Herd" problem.
        // If an external service goes down, thousands of jobs will fail and retry simultaneously,
        // effectively executing a DDoS attack on the recovering service.

        var exponent = Math.Max(0, attempt - 1);
        var calculatedDelayMs = _options.Retry.BaseDelay.TotalMilliseconds *
                                Math.Pow(_options.Retry.BackoffMultiplier, exponent);
        var cappedDelayMs = Math.Min(calculatedDelayMs, _options.Retry.MaxDelay.TotalMilliseconds);

        // JITTER: By injecting randomness, we disperse the retry wave over a wider time window.
        // This spreads out the load and allows downstream services to recover gracefully.
        //
        // The jitter window is clipped by MaxDelay, so MaxDelay remains a true operational cap.
        // This is important for on-call predictability: when an admin sets "maximum retry delay",
        // ChokaQ should not secretly exceed it by adding randomness after the cap.
        var remainingRoomMs = Math.Max(0, _options.Retry.MaxDelay.TotalMilliseconds - cappedDelayMs);
        var jitterWindowMs = Math.Min(_options.Retry.JitterMaxDelay.TotalMilliseconds, remainingRoomMs);
        var jitterMs = jitterWindowMs <= 0
            ? 0
            : Random.Shared.NextDouble() * jitterWindowMs;

        return (int)Math.Min(int.MaxValue, cappedDelayMs + jitterMs);
    }

    private int ClampDelayToSchedulerRange(TimeSpan requestedDelay)
    {
        // Retry-After can come from an external service and may be larger than what our
        // current millisecond scheduler can represent. Clamping keeps the worker stable
        // while Retry.MaxDelay remains the administrator-controlled upper bound.
        var delayMs = Math.Min(
            requestedDelay.TotalMilliseconds,
            _options.Retry.MaxDelay.TotalMilliseconds);

        return (int)Math.Clamp(delayMs, 0, int.MaxValue);
    }

    private static TimeSpan GetRandomDelay(TimeSpan min, TimeSpan max)
    {
        if (min == max)
        {
            return min;
        }

        var minMs = (long)min.TotalMilliseconds;
        var maxMs = (long)max.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Random.Shared.NextInt64(minMs, maxMs + 1));
    }

    private record JobExecutionContext(
        string JobId,
        string JobType,
        string? CreatedBy,
        string Queue,
        int Priority,
        int AttemptCount);
}
