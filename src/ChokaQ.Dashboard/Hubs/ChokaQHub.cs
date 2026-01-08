using ChokaQ.Core.Workers;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Dashboard.Hubs;

/// <summary>
/// The SignalR Hub responsible for real-time communication between the ChokaQ backend 
/// and the Dashboard UI.
/// <para>
/// Now accepts commands from the UI (Cancel, Restart).
/// </para>
/// </summary>
public class ChokaQHub : Hub
{
    private readonly IWorkerManager _workerManager;

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

    /// <summary>
    /// [NEW] Called by the Dashboard UI to request job restart.
    /// </summary>
    public async Task RestartJob(string jobId)
    {
        await _workerManager.RestartJobAsync(jobId);
    }
}