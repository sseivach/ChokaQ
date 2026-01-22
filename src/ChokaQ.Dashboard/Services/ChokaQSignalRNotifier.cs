using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
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
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10)
    {
        // PACKING: Convert 9 arguments into 1 DTO to bypass SignalR 8-arg limit
        var dto = new JobUpdateDto(
            jobId,
            type,
            status,
            attemptCount,
            executionDurationMs,
            createdBy,
            startedAtUtc,
            queue,
            priority
        );

        // Send just ONE object
        await hubContext.Clients.All.SendAsync("JobUpdated", dto);
    }

    public async Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        await hubContext.Clients.All.SendAsync("JobProgress", jobId, percentage);
    }
}