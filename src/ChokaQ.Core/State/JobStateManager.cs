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

    public async Task UpdateStateAsync(string jobId, string type, JobStatus status, int attemptCount, CancellationToken ct = default)
    {
        // 1. Persist state to storage (Critical)
        await _storage.UpdateJobStateAsync(jobId, status, ct);

        // 2. Notify UI (Non-critical)
        // We wrap this in a try-catch because a failure to update the UI 
        // should not crash the job processing logic.
        try
        {
            await _notifier.NotifyJobUpdatedAsync(jobId, type, status, attemptCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to send real-time notification for Job {JobId}: {Message}", jobId, ex.Message);
        }
    }
}