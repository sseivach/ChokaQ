using ChokaQ.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Dashboard.Hubs;

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
        await _workerManager.CancelJobAsync(jobId);
    }

    public async Task RestartJob(string jobId)
    {
        await _workerManager.RestartJobAsync(jobId);
    }

    public async Task ToggleQueue(string queueName, bool pause)
    {
        await _storage.SetQueueStateAsync(queueName, pause);
    }

    public async Task SetPriority(string jobId, int priority)
    {
        await _workerManager.SetJobPriorityAsync(jobId, priority);
    }

    public async Task UpdateQueueTimeout(string queueName, int? timeoutSeconds)
    {
        await _storage.UpdateQueueTimeoutAsync(queueName, timeoutSeconds);
    }
}