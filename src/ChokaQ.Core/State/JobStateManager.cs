using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.State;

/// <summary>
/// Manages job state transitions following the Three Pillars architecture.
/// Acts as an orchestration layer between storage persistence and SignalR notifications.
/// </summary>
/// <remarks>
/// Responsibilities:
/// - Coordinates atomic transitions between pillars (Hot → Archive, Hot → DLQ)
/// - Triggers real-time dashboard updates via IChokaQNotifier
/// - Handles notification failures gracefully (fire-and-forget with logging)
/// 
/// This separation of concerns allows JobProcessor to focus on execution logic
/// while JobStateManager handles the persistence and notification plumbing.
/// </remarks>
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

    /// <summary>
    /// Archives a successfully completed job from Hot to Archive table.
    /// Updates StatsSummary.SucceededTotal and notifies connected dashboards.
    /// </summary>
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
        await SafeNotifyAsync(() => _notifier.NotifyStatsUpdatedAsync());
    }

    /// <summary>
    /// Archives a permanently failed job from Hot to DLQ table.
    /// Called when a job exhausts all retry attempts.
    /// Updates StatsSummary.FailedTotal and notifies connected dashboards.
    /// </summary>
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
        await SafeNotifyAsync(() => _notifier.NotifyStatsUpdatedAsync());
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
        await SafeNotifyAsync(() => _notifier.NotifyStatsUpdatedAsync());
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
        await SafeNotifyAsync(() => _notifier.NotifyStatsUpdatedAsync());
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

    /// <summary>
    /// Executes UI notifications with a lightweight transient retry policy.
    /// We must NEVER throw an exception here, as it would crash the background
    /// job state machine just because the UI SignalR hub is disconnected.
    /// </summary>
    private async Task SafeNotifyAsync(Func<Task> notifyAction)
    {
        const int maxRetries = 3;
        const int delayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await notifyAction();
                return; // Success
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    // Log only on final failure to avoid log spam during network blips
                    _logger.LogWarning("Failed to send UI notification after {Retries} attempts: {Message}", maxRetries, ex.Message);
                }
                else
                {
                    await Task.Delay(delayMs);
                }
            }
        }
    }
}