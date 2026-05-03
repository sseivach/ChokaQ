using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Observability;
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
        CancellationToken ct = default,
        string? workerId = null)
    {
        // 1. Archive: Hot → Archive
        var moved = await _storage.ArchiveSucceededAsync(jobId, durationMs, ct, workerId);
        if (!moved)
        {
            // A zero-row transition is a correctness signal, not a success. It usually means
            // this worker lost ownership to zombie rescue or another administrative action.
            // Suppressing notifications prevents the dashboard from showing a false archive.
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped success notification for job {JobId}: no row was archived. WorkerId: {WorkerId}",
                jobId, workerId ?? "<admin>");
            return;
        }

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
        CancellationToken ct = default,
        string? workerId = null,
        FailureReason failureReason = FailureReason.MaxRetriesExceeded)
    {
        // 1. Archive: Hot → DLQ
        var moved = await _storage.ArchiveFailedAsync(jobId, errorDetails, ct, workerId, failureReason);
        if (!moved)
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped failure notification for job {JobId}: no row was moved to DLQ. WorkerId: {WorkerId}",
                jobId, workerId ?? "<admin>");
            return;
        }

        // 2. Notify dashboard
        await SafeNotifyAsync(() => _notifier.NotifyJobFailedAsync(jobId, queue, failureReason.ToString()));
        await SafeNotifyAsync(() => _notifier.NotifyStatsUpdatedAsync());
    }

    public async Task ArchiveCancelledAsync(
        string jobId,
        string jobType,
        string queue,
        ChokaQ.Abstractions.Enums.JobCancellationReason reason,
        string? details = null,
        CancellationToken ct = default,
        string? workerId = null)
    {
        // 1. Archive: Hot → DLQ
        var cancelledBy = details == null ? reason.ToString() : $"{reason}: {details}";
        ValueTask<bool> moveTask;

        if (reason == JobCancellationReason.Timeout)
        {
            // Timeout is not an operator cancellation. It usually means the execution budget,
            // heartbeat, or downstream dependency behavior needs tuning, so it deserves its
            // own DLQ taxonomy label instead of being hidden under "Cancelled".
            moveTask = _storage.ArchiveFailedAsync(jobId, cancelledBy, ct, workerId, FailureReason.Timeout);
        }
        else
        {
            moveTask = _storage.ArchiveCancelledAsync(jobId, cancelledBy, ct, workerId);
        }

        var moved = await moveTask;
        if (!moved)
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped cancellation notification for job {JobId}: no row was moved to DLQ. WorkerId: {WorkerId}",
                jobId, workerId ?? "<admin>");
            return;
        }

        // 2. Notify dashboard
        var failureReason = reason == JobCancellationReason.Timeout ? FailureReason.Timeout : FailureReason.Cancelled;
        await SafeNotifyAsync(() => _notifier.NotifyJobFailedAsync(jobId, queue, failureReason.ToString()));
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
        CancellationToken ct = default,
        string? workerId = null)
    {
        // 1. Reschedule in Hot table
        var moved = await _storage.RescheduleForRetryAsync(jobId, scheduledAtUtc, newAttemptCount, lastError, ct, workerId);
        if (!moved)
        {
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped retry notification for job {JobId}: no row was rescheduled. WorkerId: {WorkerId}",
                jobId, workerId ?? "<admin>");
            return;
        }

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

    public async Task<bool> MarkAsProcessingAsync(
        string jobId,
        string jobType,
        string queue,
        int priority,
        int attemptCount,
        string? createdBy,
        CancellationToken ct = default,
        string? workerId = null)
    {
        // 1. Update status in Hot table
        var moved = await _storage.MarkAsProcessingAsync(jobId, ct, workerId);
        if (!moved)
        {
            // MarkAsProcessing is the last persisted lease check before user code runs.
            // A false result means a buffered copy is stale: the job was released, reclaimed,
            // or reassigned after fetch. We suppress the UI update because no execution began.
            _logger.LogWarning(
                ChokaQLogEvents.StateTransitionNotApplied,
                "Skipped processing notification for job {JobId}: no row was marked as Processing. WorkerId: {WorkerId}",
                jobId, workerId ?? "<admin>");
            return false;
        }

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
        return true;
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
                    _logger.LogWarning(
                        ChokaQLogEvents.NotificationFailed,
                        "Failed to send UI notification after {Retries} attempts: {Message}",
                        maxRetries,
                        ex.Message);
                }
                else
                {
                    await Task.Delay(delayMs);
                }
            }
        }
    }
}
