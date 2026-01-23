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
        IJobStateManager stateManager)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
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
    public async Task ProcessJobAsync(string jobId, string jobType, string payload, string workerId, CancellationToken workerCt)
    {
        // 1. Validate State (Double-check)
        // Although the Poller fetched it, the status might have changed (e.g., cancelled by user).
        var storageDto = await _storage.GetJobAsync(jobId);
        if (storageDto?.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("[Worker {ID}] Skipping job {JobId} because it was cancelled.", workerId, jobId);
            return;
        }

        // Prepare metadata for status updates
        var meta = new JobMetadata(
            storageDto?.CreatedBy,
            storageDto?.Queue ?? "default",
            storageDto?.Priority ?? 10,
            storageDto?.AttemptCount ?? 1
        );

        // 2. Circuit Breaker Check
        // Before executing, check if the "Fuse" for this job type is intact.
        if (!_breaker.IsExecutionPermitted(jobType))
        {
            _logger.LogWarning("[CircuitBreaker] Job {JobId} skipped. Circuit for {Type} is OPEN.", jobId, jobType);

            // Wait for a configured cooldown period to avoid hammering the CPU/DB in a tight loop.
            await Task.Delay(TimeSpan.FromSeconds(CircuitBreakerDelaySeconds), workerCt);
            return;
        }

        // 3. Mark as Processing
        var startedAt = DateTime.UtcNow;
        await UpdateStatusAsync(jobId, jobType, JobStatus.Processing, meta, startedAt: startedAt, ct: workerCt);

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

            await UpdateStatusAsync(jobId, jobType, JobStatus.Succeeded, meta,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);

            _logger.LogInformation("[Worker {ID}] Job {JobId} succeeded in {Duration}ms.", workerId, jobId, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogInformation("[Worker {ID}] Job {JobId} was cancelled during execution.", workerId, jobId);

            await UpdateStatusAsync(jobId, jobType, JobStatus.Cancelled, meta,
                durationMs: sw.Elapsed.TotalMilliseconds, startedAt: startedAt, ct: workerCt);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 7. Failure Path (Retry Logic)
            await HandleErrorAsync(ex, jobId, jobType, meta, workerId, workerCt, sw.Elapsed.TotalMilliseconds, startedAt);
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
        JobMetadata meta,
        string workerId,
        CancellationToken ct,
        double durationMs,
        DateTime startedAt)
    {
        // A. Report to Circuit Breaker (May open the circuit if threshold reached)
        _breaker.ReportFailure(jobType);

        // B. Re-fetch current state to ensure we are operating on fresh data
        var currentJob = await _storage.GetJobAsync(jobId);

        // Guard: If job was cancelled while failing, respect the cancellation.
        if (currentJob == null || currentJob.Status == JobStatus.Cancelled) return;

        var currentAttempt = currentJob.AttemptCount;

        // C. Retry Logic
        if (currentAttempt <= MaxRetries)
        {
            var nextAttempt = currentAttempt + 1;
            var delayMs = CalculateBackoff(currentAttempt);

            _logger.LogWarning(ex, "[Worker {ID}] Job {JobId} failed after {Duration}ms. Retrying in {Delay}ms (Attempt {Next}).",
                workerId, jobId, durationMs, delayMs, nextAttempt);

            // Increment attempt counter in DB
            await _storage.IncrementJobAttemptAsync(jobId, nextAttempt);

            // Move back to Pending so the Poller picks it up again later.
            // TODO: In a future version, set ScheduledAtUtc = Now + delayMs to delay pickup.
            await UpdateStatusAsync(jobId, jobType, JobStatus.Pending, meta with { AttemptCount = nextAttempt }, ct: ct);

            // Short wait to prevent instant re-pickup if the DB poll is fast
            await Task.Delay(delayMs, ct);
        }
        else
        {
            // D. Permanent Failure
            _logger.LogError(ex, "[Worker {ID}] Job {JobId} FAILED permanently after {Duration}ms.", workerId, durationMs);

            await UpdateStatusAsync(jobId, jobType, JobStatus.Failed, meta,
                durationMs: durationMs, startedAt: startedAt, ct: ct);
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
    private Task UpdateStatusAsync(string id, string type, JobStatus status, JobMetadata meta,
        double? durationMs = null, DateTime? startedAt = null, CancellationToken ct = default)
    {
        return _stateManager.UpdateStateAsync(
            id, type, status, meta.AttemptCount,
            executionDurationMs: durationMs,
            createdBy: meta.CreatedBy,
            startedAtUtc: startedAt,
            queue: meta.Queue,
            priority: meta.Priority,
            ct: ct
        );
    }

    // Lightweight record to pass immutable metadata through the pipeline
    private record JobMetadata(string? CreatedBy, string Queue, int Priority, int AttemptCount);
}