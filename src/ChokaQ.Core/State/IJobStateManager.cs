using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.State;

/// <summary>
/// Manages the state transitions of jobs.
/// Encapsulates the logic of persisting state changes to storage and notifying the UI via real-time events.
/// </summary>
public interface IJobStateManager
{
    /// <summary>
    /// Updates the job status in storage and broadcasts the change to subscribers (Dashboard).
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="type">The job type name (for UI display).</param>
    /// <param name="status">The new status to set.</param>
    /// <param name="attemptCount">The current attempt count.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateStateAsync(string jobId, string type, JobStatus status, int attemptCount, CancellationToken ct = default);
}