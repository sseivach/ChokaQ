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
    Rejected = 4
}
