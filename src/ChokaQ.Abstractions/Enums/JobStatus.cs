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
    /// Job is currently being executed by a worker.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Job has finished execution successfully.
    /// </summary>
    Succeeded = 2,

    /// <summary>
    /// Job execution failed due to an unhandled exception.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Job was cancelled before completion.
    /// </summary>
    Cancelled = 4
}