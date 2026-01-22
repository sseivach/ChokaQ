using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface IChokaQNotifier
{
    Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10
    );

    Task NotifyJobProgressAsync(string jobId, int percentage);
}