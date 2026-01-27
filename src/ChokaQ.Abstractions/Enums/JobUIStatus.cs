namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Represents the high-level status of a job as seen by the User Interface.
/// Decouples internal DB state (Pending/Fetched) from UX state (Retrying/Morgue).
/// </summary>
public enum JobUIStatus
{
    /// <summary>
    /// Job is waiting in the queue.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Job is currently being executed by a worker.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Job completed successfully and has been archived.
    /// </summary>
    Succeeded = 2,

    /// <summary>
    /// Job failed but is scheduled for a retry (Temporary Failure).
    /// </summary>
    Retrying = 3,

    /// <summary>
    /// Job failed permanently and moved to the Dead Letter Queue (DLQ).
    /// </summary>
    Morgue = 4,

    /// <summary>
    /// Job was manually cancelled by a user.
    /// </summary>
    Cancelled = 5
}