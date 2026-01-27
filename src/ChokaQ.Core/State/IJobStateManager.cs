using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Core.State;

/// <summary>
/// Manages the state transitions of jobs in Three Pillars architecture.
/// Coordinates storage operations and real-time notifications.
/// </summary>
public interface IJobStateManager
{
    /// <summary>
    /// Archives a succeeded job: Hot → Archive.
    /// Notifies dashboard about the transition.
    /// </summary>
    Task ArchiveSucceededAsync(
        string jobId,
        string jobType,
        string queue,
        double? durationMs = null,
        CancellationToken ct = default);

    /// <summary>
    /// Archives a failed job: Hot → DLQ.
    /// Notifies dashboard about the transition.
    /// </summary>
    Task ArchiveFailedAsync(
        string jobId,
        string jobType,
        string queue,
        string errorDetails,
        CancellationToken ct = default);

    /// <summary>
    /// Archives a cancelled job: Hot → DLQ.
    /// Notifies dashboard about the transition.
    /// </summary>
    Task ArchiveCancelledAsync(
        string jobId,
        string jobType,
        string queue,
        string? cancelledBy = null,
        CancellationToken ct = default);

    /// <summary>
    /// Reschedules a job for retry (stays in Hot).
    /// Notifies dashboard about the update.
    /// </summary>
    Task RescheduleForRetryAsync(
        string jobId,
        string jobType,
        string queue,
        int priority,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default);

    /// <summary>
    /// Updates job to Processing status (stays in Hot).
    /// Notifies dashboard about the update.
    /// </summary>
    Task MarkAsProcessingAsync(
        string jobId,
        string jobType,
        string queue,
        int priority,
        int attemptCount,
        string? createdBy,
        CancellationToken ct = default);
}
