using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.State;

/// <summary>
/// Manages state transitions in Three Pillars architecture.
/// Coordinates storage operations and real-time SignalR notifications.
/// </summary>
public class JobStateManager : IJobStateManager
{
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<JobStateManager> _logger;

    public JobStateManager(
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<JobStateManager> logger)
    {
        _storage = storage;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task ArchiveSucceededAsync(
        string jobId,
        string jobType,
        string queue,
        double? durationMs = null,
        CancellationToken ct = default)
    {
        // 1. Archive: Hot → Archive
        await _storage.ArchiveSucceededAsync(jobId, durationMs, ct);

        // 2. Notify dashboard
        await SafeNotifyAsync(() => _notifier.NotifyJobArchivedAsync(jobId, queue));
    }

    public async Task ArchiveFailedAsync(
        string jobId,
        string jobType,
        string queue,
        string errorDetails,
        CancellationToken ct = default)
    {
        // 1. Archive: Hot → DLQ
        await _storage.ArchiveFailedAsync(jobId, errorDetails, ct);

        // 2. Notify dashboard
        await SafeNotifyAsync(() => _notifier.NotifyJobFailedAsync(jobId, queue, "MaxRetriesExceeded"));
    }

    public async Task ArchiveCancelledAsync(
        string jobId,
        string jobType,
        string queue,
        string? cancelledBy = null,
        CancellationToken ct = default)
    {
        // 1. Archive: Hot → DLQ
        await _storage.ArchiveCancelledAsync(jobId, cancelledBy, ct);

        // 2. Notify dashboard
        await SafeNotifyAsync(() => _notifier.NotifyJobFailedAsync(jobId, queue, "Cancelled"));
    }

    public async Task RescheduleForRetryAsync(
        string jobId,
        string jobType,
        string queue,
        int priority,
        DateTime scheduledAtUtc,
        int newAttemptCount,
        string lastError,
        CancellationToken ct = default)
    {
        // 1. Reschedule in Hot table
        await _storage.RescheduleForRetryAsync(jobId, scheduledAtUtc, newAttemptCount, lastError, ct);

        // 2. Notify dashboard
        var update = new JobUpdateDto(
            JobId: jobId,
            Type: jobType,
            Queue: queue,
            Status: JobStatus.Pending,
            AttemptCount: newAttemptCount,
            Priority: priority,
            DurationMs: null,
            CreatedBy: null,
            StartedAtUtc: null
        );
        await SafeNotifyAsync(() => _notifier.NotifyJobUpdatedAsync(update));
    }

    public async Task MarkAsProcessingAsync(
        string jobId,
        string jobType,
        string queue,
        int priority,
        int attemptCount,
        string? createdBy,
        CancellationToken ct = default)
    {
        // 1. Update status in Hot table
        await _storage.MarkAsProcessingAsync(jobId, ct);

        // 2. Notify dashboard
        var now = DateTime.UtcNow;
        var update = new JobUpdateDto(
            JobId: jobId,
            Type: jobType,
            Queue: queue,
            Status: JobStatus.Processing,
            AttemptCount: attemptCount,
            Priority: priority,
            DurationMs: null,
            CreatedBy: createdBy,
            StartedAtUtc: now
        );
        await SafeNotifyAsync(() => _notifier.NotifyJobUpdatedAsync(update));
    }

    private async Task SafeNotifyAsync(Func<Task> notifyAction)
    {
        try
        {
            await notifyAction();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to send notification: {Message}", ex.Message);
        }
    }
}
