using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Dashboard.Services;

internal class ChokaQSignalRNotifier(IHubContext<ChokaQHub> hubContext) : IChokaQNotifier
{
    public async Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null)
    {
        await hubContext.Clients.All.SendAsync(
            "JobUpdated",
            jobId,
            type,
            (int)status,
            attemptCount,
            executionDurationMs,
            createdBy,
            startedAtUtc
        );
    }

    public async Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        await hubContext.Clients.All.SendAsync("JobProgress", jobId, percentage);
    }
}