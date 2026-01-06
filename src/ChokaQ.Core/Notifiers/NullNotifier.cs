using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.Notifiers;

/// <summary>
/// A silent notifier used when no UI/Dashboard is configured.
/// This prevents dependency injection errors in Console Apps or Workers.
/// </summary>
internal class NullNotifier : IChokaQNotifier
{
    public Task NotifyJobUpdatedAsync(string jobId, JobStatus status, int attemptCount)
    {
        // Do nothing.
        return Task.CompletedTask;
    }

    public Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        return Task.CompletedTask;
    }
}