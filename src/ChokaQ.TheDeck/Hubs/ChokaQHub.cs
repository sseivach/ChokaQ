using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChokaQ.TheDeck.Hubs;

/// <summary>
/// SignalR Hub for The Deck UI.
/// Provides real-time bidirectional communication and command handling.
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
        _logger.LogInformation("TheDeck: CancelJob requested for {JobId}", jobId);
        await _workerManager.CancelJobAsync(jobId);
    }

    public async Task RestartJob(string jobId)
    {
        _logger.LogInformation("TheDeck: RestartJob requested for {JobId}", jobId);
        await _workerManager.RestartJobAsync(jobId);
    }

    public async Task ResurrectJob(string jobId, string? newPayload = null, int? newPriority = null)
    {
        _logger.LogInformation("TheDeck: ResurrectJob requested for {JobId}", jobId);
        var updates = new JobDataUpdateDto(newPayload, null, newPriority);
        await _storage.ResurrectAsync(jobId, updates.HasChanges ? updates : null, "TheDeck Admin");
    }

    public async Task ToggleQueue(string queueName, bool pause)
    {
        _logger.LogInformation("TheDeck: ToggleQueue {Queue} -> Paused={Pause}", queueName, pause);
        await _storage.SetQueuePausedAsync(queueName, pause);
    }

    public async Task SetPriority(string jobId, int priority)
    {
        _logger.LogInformation("TheDeck: SetPriority {JobId} -> {Priority}", jobId, priority);
        await _workerManager.SetJobPriorityAsync(jobId, priority);
    }

    public async Task UpdateQueueTimeout(string queueName, int? timeoutSeconds)
    {
        _logger.LogInformation("TheDeck: UpdateQueueTimeout {Queue} -> {Timeout}s", queueName, timeoutSeconds);
        await _storage.SetQueueZombieTimeoutAsync(queueName, timeoutSeconds);
    }

    public async Task PurgeDLQ(string[] jobIds)
    {
        _logger.LogWarning("TheDeck: PurgeDLQ requested for {Count} jobs", jobIds.Length);
        await _storage.PurgeDLQAsync(jobIds);
    }

    public async Task<bool> EditJob(string jobId, string? newPayload, string? newTags, int? newPriority)
    {
        _logger.LogInformation("TheDeck: EditJob requested for {JobId}", jobId);
        var updates = new JobDataUpdateDto(newPayload, newTags, newPriority);
        return await _storage.UpdateJobDataAsync(jobId, updates, "TheDeck Admin");
    }
}
