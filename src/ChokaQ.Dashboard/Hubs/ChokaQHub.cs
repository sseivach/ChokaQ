using ChokaQ.Core.Workers; // Need access to WorkerManager
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Dashboard.Hubs;

/// <summary>
/// The SignalR Hub responsible for real-time communication.
/// Now accepts commands from the UI.
/// </summary>
public class ChokaQHub : Hub
{
    private readonly IWorkerManager _workerManager;

    // Inject the manager
    public ChokaQHub(IWorkerManager workerManager)
    {
        _workerManager = workerManager;
    }

    /// <summary>
    /// Called by the Dashboard UI to request job cancellation.
    /// </summary>
    public async Task CancelJob(string jobId)
    {
        await _workerManager.CancelJobAsync(jobId);
    }
}