using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Dashboard.Hubs;

/// <summary>
/// SignalR Hub for Dashboard real-time operations.
/// Provides bidirectional communication for job management.
/// </summary>
public class ChokaQHub : Hub
{
    private readonly IWorkerManager _workerManager;
    private readonly IJobStorage _storage;
    private readonly ILogger<ChokaQHub> _logger;

    public ChokaQHub(
        IWorkerManager workerManager,
        IJobStorage storage,
        ILogger<ChokaQHub> logger)
    {
        _workerManager = workerManager;
        _storage = storage;
        _logger = logger;
    }

    public async Task CancelJob(string jobId)
    {
        _logger.LogInformation("Dashboard: CancelJob requested for {JobId}", jobId);
        await _workerManager.CancelJobAsync(jobId);
    }

    public async Task RestartJob(string jobId)
    {
        _logger.LogInformation("Dashboard: RestartJob requested for {JobId}", jobId);
        await _workerManager.RestartJobAsync(jobId);
    }

    /// <summary>
    /// Resurrects a job from DLQ with optional data updates.
    /// </summary>
    public async Task ResurrectJob(string jobId, string? newPayload = null, int? newPriority = null)
    {
        _logger.LogInformation("Dashboard: ResurrectJob requested for {JobId}", jobId);
        var updates = new JobDataUpdateDto(newPayload, null, newPriority);
        await _storage.ResurrectAsync(jobId, updates.HasChanges ? updates : null, "Dashboard Admin");
    }

    public async Task ToggleQueue(string queueName, bool pause)
    {
        _logger.LogInformation("Dashboard: ToggleQueue {Queue} -> Paused={Pause}", queueName, pause);
        await _storage.SetQueuePausedAsync(queueName, pause);
    }

    public async Task SetPriority(string jobId, int priority)
    {
        _logger.LogInformation("Dashboard: SetPriority {JobId} -> {Priority}", jobId, priority);
        await _workerManager.SetJobPriorityAsync(jobId, priority);
    }

    public async Task UpdateQueueTimeout(string queueName, int? timeoutSeconds)
    {
        _logger.LogInformation("Dashboard: UpdateQueueTimeout {Queue} -> {Timeout}s", queueName, timeoutSeconds);
        await _storage.SetQueueZombieTimeoutAsync(queueName, timeoutSeconds);
    }

    /// <summary>
    /// Permanently deletes jobs from DLQ.
    /// </summary>
    public async Task PurgeDLQ(string[] jobIds)
    {
        _logger.LogWarning("Dashboard: PurgeDLQ requested for {Count} jobs", jobIds.Length);
        await _storage.PurgeDLQAsync(jobIds);
    }

    /// <summary>
    /// Edits job data (only for Pending jobs in Hot table).
    /// </summary>
    public async Task<bool> EditJob(string jobId, string? newPayload, string? newTags, int? newPriority)
    {
        _logger.LogInformation("Dashboard: EditJob requested for {JobId}", jobId);
        var updates = new JobDataUpdateDto(newPayload, newTags, newPriority);
        return await _storage.UpdateJobDataAsync(jobId, updates, "Dashboard Admin");
    }
}
