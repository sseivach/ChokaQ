using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    /// <summary>
    /// Notifies connected clients about a job status change.
    /// </summary>
    Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobUIStatus status,
        int attemptCount,
        double? executionDurationMs,
        string? createdBy,
        DateTime? startedAtUtc,
        string queue,
        int priority
    );

    Task NotifyJobProgressAsync(string jobId, int percentage);
}