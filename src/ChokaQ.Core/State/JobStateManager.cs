using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.State;

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

    public async Task UpdateStateAsync(
        string jobId,
        string type,
        JobStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10,
        string? errorDetails = null,
        CancellationToken ct = default)
    {
        // 1. Persist to DB
        await _storage.UpdateJobStateAsync(jobId, status, errorDetails, ct);

        // 2. Notify UI with FULL context
        try
        {
            await _notifier.NotifyJobUpdatedAsync(
                jobId,
                type,
                status,
                attemptCount,
                executionDurationMs,
                createdBy,
                startedAtUtc,
                queue,
                priority
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to notify: {Message}", ex.Message);
        }
    }

    public async Task RescheduleJobAsync(
        string jobId,
        string type,
        DateTime scheduledAtUtc,
        int attemptCount,
        string errorDetails,
        string queue,
        int priority,
        CancellationToken ct = default)
    {
        await _storage.RescheduleJobAsync(jobId, scheduledAtUtc, attemptCount, errorDetails, ct);

        try
        {
            await _notifier.NotifyJobUpdatedAsync(
                jobId,
                type,
                JobStatus.Pending,
                attemptCount,
                null,
                null,
                null,
                queue,
                priority
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to notify UI about reschedule: {Message}", ex.Message);
        }
    }
}