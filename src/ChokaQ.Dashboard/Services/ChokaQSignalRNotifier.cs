using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Dashboard.Services;

internal class ChokaQSignalRNotifier(
    IHubContext<ChokaQHub> hubContext,
    ILogger<ChokaQSignalRNotifier> logger) : IChokaQNotifier
{
    public async Task NotifyJobUpdatedAsync(string jobId, JobStatus status)
    {
        // logger.LogDebug("Pushing update to UI via SignalR...");
        await hubContext.Clients.All.SendAsync("JobUpdated", jobId, (int)status);
    }
}