using ChokaQ.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Dashboard.Hubs;

/// <summary>
/// SignalR Hub for real-time communication between the Server (Backend) and the Dashboard (UI).
/// Handles commands like cancelling jobs, restarting jobs, and managing queues.
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

    /// <summary>
    /// Cancels a running or pending job.
    /// </summary>
    /// <param name="jobId">The unique ID of the job.</param>
    public async Task CancelJob(string jobId)
    {
        _logger.LogInformation("Dashboard requested cancellation for Job {JobId}.", jobId);
        await _workerManager.CancelJobAsync(jobId);
    }

    /// <summary>
    /// Restarts a finished (failed/succeeded/cancelled) job.
    /// </summary>
    /// <param name="jobId">The unique ID of the job.</param>
    public async Task RestartJob(string jobId)
    {
        _logger.LogInformation("Dashboard requested restart for Job {JobId}.", jobId);
        await _workerManager.RestartJobAsync(jobId);
    }

    /// <summary>
    /// Toggles the state (Paused/Running) of a specific queue.
    /// This method fixes the 'Method does not exist' error.
    /// </summary>
    /// <param name="queueName">The name of the queue (e.g., 'default').</param>
    /// <param name="pause">True to pause processing, False to resume.</param>
    public async Task ToggleQueue(string queueName, bool pause)
    {
        _logger.LogInformation("Dashboard requested to {Action} queue '{Queue}'.",
            pause ? "PAUSE" : "RESUME", queueName);

        await _storage.SetQueueStateAsync(queueName, pause);
    }
}