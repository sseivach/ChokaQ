using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Execution;
using ChokaQ.Core.State;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ChokaQ.Core.Processing;

/// <summary>
/// The core execution engine for a single job.
/// Orchestrates the lifecycle: Fetch -> Circuit Check -> Execute -> Success/Failure -> Retry.
/// </summary>
public class JobProcessor : IJobProcessor
{
    private readonly IJobStorage _storage;
    private readonly ILogger<JobProcessor> _logger;
    private readonly ICircuitBreaker _breaker;
    private readonly IJobDispatcher _dispatcher;
    private readonly IJobStateManager _stateManager;

    // Registry of tokens for currently running jobs to allow real-time cancellation.
    // Key: JobId, Value: CancellationTokenSource linked to the worker token.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeJobTokens = new();

    // --- Configuration (Injected via properties to allow runtime adjustments) ---

    /// <summary>
    /// Maximum number of retries before marking a job as Failed.
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
    /// Prevents tight loops when a downstream service is down.
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
    /// thread-safe method to signal cancellation for a running job.
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
        var context = new JobExecutionContext(createdBy, meta.Queue, meta.Priority, attemptCount);

        // 3. Circuit Breaker
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);

            var currentJob = await _storage.GetJobAsync(jobId);
            if (currentJob != null)
            {
                await _stateManager.RescheduleJobAsync(
                    jobId,
                    jobType,
                    DateTime.UtcNow.AddSeconds(5),
                    currentJob.AttemptCount,
                    "Circuit Breaker Open",
                    meta.Queue,
                    meta.Priority,
                    workerCt);
            }
            return;
        }

        // 3. Mark as Processing
        var startedAt = DateTime.UtcNow;
        await UpdateStatusAsync(jobId, jobType, JobStatus.Processing, context, startedAt: startedAt, ct: workerCt);

        // 4. Execution Setup
        // Create a linked token so the job cancels if:
        // A) The Worker shuts down (workerCt)
        // B) The User cancels this specific job via Dashboard (CancelJob)
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(workerCt);
        _activeJobTokens.TryAdd(jobId, jobCts);

        var sw = Stopwatch.StartNew();
        try
        {
            // 5. Dispatch
            // The dispatcher routes the job to the correct Handler (Bus Mode) or Pipe (Raw Mode).
            await _dispatcher.DispatchAsync(jobId, jobType, payload, jobCts.Token);
            sw.Stop();

            // 6. Success Path
            _breaker.ReportSuccess(jobType); // Tell breaker "Service is healthy"

            await UpdateStatusAsync(jobId, jobType, JobStatus.Succeeded, context,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);

            _logger.LogInformation("[Worker {ID}] Job {JobId} succeeded in {Duration}ms.", workerId, jobId, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogInformation("[Worker {ID}] Job {JobId} was cancelled during execution.", workerId, jobId);

            await UpdateStatusAsync(jobId, jobType, JobStatus.Cancelled, context,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 7. Failure Path (Retry Logic)
            await HandleErrorAsync(ex, jobId, jobType, context, workerId, workerCt, sw.Elapsed.TotalMilliseconds, startedAt);
        }
        finally
        {
            // Cleanup token registry
            _activeJobTokens.TryRemove(jobId, out _);
        }
    }

    private async Task HandleErrorAsync(
        Exception ex,
        string jobId,
        string jobType,
        JobExecutionContext context,
        string workerId,
        CancellationToken ct,
        double durationMs,
        DateTime startedAt)
    {
        // 1. Notify monitoring system (Circuit Breaker metrics)
        _breaker.ReportFailure(jobType);

        var currentAttempt = context.AttemptCount;

        // 2. Decision: Retry or Fail Permanently?
        if (currentAttempt < MaxRetries)
        {
            var nextAttempt = currentAttempt + 1;
            var delayMs = CalculateBackoff(nextAttempt);
            var scheduledAt = DateTime.UtcNow.AddMilliseconds(delayMs);

            // Log warning (Ensure placeholders match arguments count to avoid FormatException)
            _logger.LogWarning(ex,
                "[Worker {WorkerId}] Job {JobId} failed. Smart Retry #{Attempt} scheduled in {Delay}ms.",
                workerId, jobId, nextAttempt, delayMs);

            // Smart reschedule in storage (pushes execution to the future)
            await _stateManager.RescheduleJobAsync(
                jobId,
                jobType,
                scheduledAt,
                nextAttempt,
                ex.Message,
                context.Queue,
                context.Priority,
                ct);
        }
        else
        {
            // Log permanent failure
            _logger.LogError(ex,
                "[Worker {WorkerId}] Job {JobId} failed permanently after {Retries} attempts. Error: {Error}",
                workerId, jobId, MaxRetries, ex.Message);

            // Mark job as Failed
            await UpdateStatusAsync(
                jobId,
                jobType,
                JobStatus.Failed,
                context,
                ex.Message,
                durationMs,
                startedAt,
                ct);
        }
    }

    /// <summary>
    /// Calculates exponential backoff with jitter to prevent Thundering Herd problem.
    /// Formula: (Base * 2^(attempt-1)) + Jitter
    /// </summary>
    private int CalculateBackoff(int attempt)
    {
        var baseDelay = RetryDelaySeconds * Math.Pow(2, attempt - 1);

        // Cap the delay at 1 hour to prevent excessively long waits
        if (baseDelay > 3600) baseDelay = 3600;

        var jitter = Random.Shared.Next(0, 1000); // 0-1 sec jitter
        return (int)(baseDelay * 1000) + jitter;
    }

    /// <summary>
    /// Helper to update state in Storage + Notify Dashboard via SignalR.
    /// </summary>
    private async Task UpdateStatusAsync(
        string jobId,
        string jobType,
        JobStatus status,
        JobExecutionContext context,
        string? result = null,
        double durationMs = 0,
        DateTime? startedAt = null,
        CancellationToken ct = default)
    {
        // 1. Notify State Manager
        await _stateManager.UpdateStateAsync(
            jobId: jobId,
            type: jobType,
            status: status,
            attemptCount: context.AttemptCount,
            executionDurationMs: durationMs,
            createdBy: context.CreatedBy,
            startedAtUtc: startedAt,
            queue: context.Queue,
            priority: context.Priority,
            errorDetails: result,
            ct: ct);

        // 2. Notify Circuit Breaker (Success counts towards closing the circuit)
        if (status == JobStatus.Succeeded)
        {
            _breaker.ReportSuccess(jobType);
        }
    }

    // Lightweight record to pass immutable metadata through the pipeline
    private record JobExecutionContext(string? CreatedBy, string Queue, int Priority, int AttemptCount);
}