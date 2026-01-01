namespace ChokaQ.Abstractions;

/// <summary>
/// Represents the current state of a background job.
/// </summary>
public enum JobStatus
{
    Pending = 0,
    Processing = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}