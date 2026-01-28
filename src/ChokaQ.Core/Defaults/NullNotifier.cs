using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Notifications;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// No-op implementation of IChokaQNotifier.
/// Used when SignalR dashboard is not enabled.
/// </summary>
internal class NullNotifier : IChokaQNotifier
{
    public Task NotifyJobUpdatedAsync(JobUpdateDto update) => Task.CompletedTask;

    public Task NotifyJobProgressAsync(string jobId, int percentage) => Task.CompletedTask;

    public Task NotifyJobArchivedAsync(string jobId, string queue) => Task.CompletedTask;

    public Task NotifyJobFailedAsync(string jobId, string queue, string reason) => Task.CompletedTask;

    public Task NotifyJobResurrectedAsync(string jobId, string queue) => Task.CompletedTask;

    public Task NotifyJobsPurgedAsync(string[] jobIds, string source) => Task.CompletedTask;

    public Task NotifyQueueStateChangedAsync(string queueName, bool isPaused) => Task.CompletedTask;

    public Task NotifyStatsUpdatedAsync() => Task.CompletedTask;
}
