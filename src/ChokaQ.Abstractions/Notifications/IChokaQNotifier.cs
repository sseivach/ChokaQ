using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Notifications;

/// <summary>
/// Contract for real-time notifications via SignalR.
/// Supports Three Pillars architecture transitions.
/// </summary>
public interface IChokaQNotifier
{
    /// <summary>
    /// Notifies clients about a job status update in Hot table.
    /// </summary>
    Task NotifyJobUpdatedAsync(JobUpdateDto update);

    /// <summary>
    /// Notifies clients about job progress (0-100%).
    /// </summary>
    Task NotifyJobProgressAsync(string jobId, int percentage);

    /// <summary>
    /// Notifies clients that a job was archived to success history.
    /// Dashboard should move job from Active to History view.
    /// </summary>
    Task NotifyJobArchivedAsync(string jobId, string queue);

    /// <summary>
    /// Notifies clients that a job was moved to DLQ (failed/cancelled/zombie).
    /// Dashboard should move job from Active to Morgue view.
    /// </summary>
    Task NotifyJobFailedAsync(string jobId, string queue, string reason);

    /// <summary>
    /// Notifies clients that a job was resurrected from DLQ.
    /// Dashboard should move job from Morgue to Active view.
    /// </summary>
    Task NotifyJobResurrectedAsync(string jobId, string queue);

    /// <summary>
    /// Notifies clients that jobs were purged (permanently deleted).
    /// </summary>
    Task NotifyJobsPurgedAsync(string[] jobIds, string source);

    /// <summary>
    /// Notifies clients about queue state change (paused/resumed).
    /// </summary>
    Task NotifyQueueStateChangedAsync(string queueName, bool isPaused);

    /// <summary>
    /// Notifies clients about statistics update.
    /// Called after batch operations to refresh dashboard counters.
    /// </summary>
    Task NotifyStatsUpdatedAsync();
}
