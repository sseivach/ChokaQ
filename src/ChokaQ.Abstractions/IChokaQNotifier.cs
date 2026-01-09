using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    // Updated signature with 'createdBy' and 'startedAtUtc'
    Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null
    );

    Task NotifyJobProgressAsync(string jobId, int percentage);
}