using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.Defaults;

internal class NullNotifier : IChokaQNotifier
{
    public Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobUIStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10)
    {
        return Task.CompletedTask;
    }

    public Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        return Task.CompletedTask;
    }
}