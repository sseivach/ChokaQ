namespace ChokaQ.Core.Processing;

/// <summary>
/// Encapsulates the complete lifecycle of processing a single job.
/// Handles Circuit Breaker checks, execution, success/failure reporting, and retries.
/// </summary>
public interface IJobProcessor
{
    /// <summary>
    /// Compatibility configuration: maximum total execution attempts.
    /// </summary>
    /// <remarks>
    /// Prefer ChokaQOptions.Retry.MaxAttempts for new code. This property remains so The Deck
    /// and older hosts can tune retry policy without taking a breaking API change.
    /// </remarks>
    int MaxRetries { get; set; }

    /// <summary>
    /// Compatibility configuration: base retry delay in seconds.
    /// </summary>
    /// <remarks>
    /// Prefer ChokaQOptions.Retry.BaseDelay for new code.
    /// </remarks>
    int RetryDelaySeconds { get; set; }

    /// <summary>
    /// Compatibility configuration: delay in seconds when the Circuit Breaker blocks execution.
    /// </summary>
    /// <remarks>
    /// Prefer ChokaQOptions.Retry.CircuitBreakerDelay for new code.
    /// </remarks>
    int CircuitBreakerDelaySeconds { get; set; }

    /// <summary>
    /// Processes a single job with full resilience logic.
    /// </summary>
    /// <param name="jobId">Unique Job ID.</param>
    /// <param name="jobType">Type key or class name.</param>
    /// <param name="payload">JSON payload.</param>
    /// <param name="workerId">The ID of the worker thread.</param>
    /// <param name="attemptCount">Persisted count of attempts that have already started execution.</param>
    /// <param name="workerCt">The worker's cancellation token.</param>
    Task ProcessJobAsync(
        string jobId,
        string jobType,
        string payload,
        string workerId,
        int attemptCount,
        string? createdBy,
        DateTime? scheduledAtUtc,
        DateTime createdAtUtc,
        CancellationToken workerCt);
}
