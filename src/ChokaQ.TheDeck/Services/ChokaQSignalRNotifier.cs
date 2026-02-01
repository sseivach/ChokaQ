using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.TheDeck.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.TheDeck.Services;

/// <summary>
/// SignalR implementation of IChokaQNotifier for The Deck UI updates.
/// </summary>
internal class ChokaQSignalRNotifier(IHubContext<ChokaQHub> hubContext) : IChokaQNotifier
{
    public async Task NotifyJobUpdatedAsync(JobUpdateDto update)
    {
        await hubContext.Clients.All.SendAsync("JobUpdated", update);
    }

    public async Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        await hubContext.Clients.All.SendAsync("JobProgress", jobId, percentage);
    }

    public async Task NotifyJobArchivedAsync(string jobId, string queue)
    {
        await hubContext.Clients.All.SendAsync("JobArchived", jobId, queue);
    }

    public async Task NotifyJobFailedAsync(string jobId, string queue, string reason)
    {
        await hubContext.Clients.All.SendAsync("JobFailed", jobId, queue, reason);
    }

    public async Task NotifyJobResurrectedAsync(string jobId, string queue)
    {
        await hubContext.Clients.All.SendAsync("JobResurrected", jobId, queue);
    }

    public async Task NotifyJobsPurgedAsync(string[] jobIds, string source)
    {
        await hubContext.Clients.All.SendAsync("JobsPurged", jobIds, source);
    }

    public async Task NotifyQueueStateChangedAsync(string queueName, bool isPaused)
    {
        await hubContext.Clients.All.SendAsync("QueueStateChanged", queueName, isPaused);
    }

    public async Task NotifyStatsUpdatedAsync()
    {
        await hubContext.Clients.All.SendAsync("StatsUpdated");
    }
}
