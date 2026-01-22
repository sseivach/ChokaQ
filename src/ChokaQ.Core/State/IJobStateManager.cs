using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.State;

/// <summary>
/// Manages the state transitions of jobs.
/// Encapsulates the logic of persisting state changes to storage and notifying the UI via real-time events.
/// </summary>
public interface IJobStateManager
{
    Task UpdateStateAsync(
        string jobId,
        string type,
        JobStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10,
        CancellationToken ct = default
    );
}