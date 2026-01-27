namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Represents the lifecycle states of a background job.
/// 
/// Three Pillars Architecture:
/// - Hot Table (JobsHot): Pending, Fetched, Processing
/// - Archive (JobsArchive): Jobs that completed successfully (virtual Succeeded state)
/// - DLQ (JobsDLQ): Jobs that failed, were cancelled, or became zombies
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// Job is queued and waiting to be fetched by a worker.
    /// Location: Hot Table
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Job was fetched from database into worker memory buffer.
    /// Waiting for processing slot (semaphore).
    /// Location: Hot Table
    /// </summary>
    Fetched = 1,

    /// <summary>
    /// Job is actively being executed by a worker.
    /// HeartbeatUtc is updated periodically.
    /// Location: Hot Table
    /// </summary>
    Processing = 2,

    /// <summary>
    /// Job completed successfully.
    /// Note: In Three Pillars, succeeded jobs are moved to Archive table.
    /// This status is used for SignalR notifications and In-Memory mode.
    /// </summary>
    Succeeded = 3,

    /// <summary>
    /// Job failed after exhausting all retry attempts.
    /// Note: In Three Pillars, failed jobs are moved to DLQ table.
    /// This status is used for SignalR notifications and In-Memory mode.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Job was cancelled by an administrator.
    /// Note: In Three Pillars, cancelled jobs are moved to DLQ table.
    /// This status is used for SignalR notifications and In-Memory mode.
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// Job was detected as zombie (Processing with expired heartbeat).
    /// Note: In Three Pillars, zombie jobs are moved to DLQ table.
    /// This status is used for SignalR notifications and In-Memory mode.
    /// </summary>
    Zombie = 6
}
