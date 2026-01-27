namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Represents the lifecycle states of a background job.
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// Job has been received and queued but processing has not started yet.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Job was retrieved from the database, is currently in RAM, and is waiting for a semaphore.
    /// </summary>
    Fetched = 1,

    /// <summary>
    /// Job is currently being executed by a worker.
    /// </summary>
    Processing = 2
}