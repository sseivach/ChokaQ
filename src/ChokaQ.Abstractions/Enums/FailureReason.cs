namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Categorizes why a job was moved to the Dead Letter Queue (DLQ).
/// Used for filtering, analytics, and operational dashboards.
/// </summary>
public enum FailureReason
{
    /// <summary>
    /// Job failed after exhausting all retry attempts.
    /// ErrorDetails contains the last exception stack trace.
    /// </summary>
    MaxRetriesExceeded = 0,

    /// <summary>
    /// Job was manually cancelled by an administrator.
    /// ErrorDetails contains the admin identity and timestamp.
    /// </summary>
    Cancelled = 1,

    /// <summary>
    /// Job was detected as zombie (processing timed out).
    /// Worker heartbeat exceeded the configured threshold.
    /// ErrorDetails contains timeout details.
    /// </summary>
    Zombie = 2,

    /// <summary>
    /// Job failed due to circuit breaker being open.
    /// Too many failures for this job type; execution was blocked.
    /// </summary>
    CircuitBreakerOpen = 3,

    /// <summary>
    /// Job was rejected during enqueue (e.g., validation failure, queue full).
    /// Rare case for jobs that never started processing.
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Downstream dependency throttled the job (for example HTTP 429 / rate limiting).
    /// Operators should usually reduce concurrency, increase delay, or check quotas.
    /// </summary>
    Throttled = 5,

    /// <summary>
    /// Job failed with a non-retryable poison-pill error.
    /// Retrying would waste worker capacity until the payload or handler is fixed.
    /// </summary>
    FatalError = 6,

    /// <summary>
    /// Job execution exceeded its allowed runtime or was cancelled by the execution timeout.
    /// This is separated from admin cancellation because the remediation path is different.
    /// </summary>
    Timeout = 7,

    /// <summary>
    /// Job exhausted retries after ordinary retryable failures.
    /// This keeps standard transient exhaustion distinct from fatal, timeout, and throttling paths.
    /// </summary>
    Transient = 8
}
