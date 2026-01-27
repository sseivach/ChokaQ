using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.Dashboard.Services;

public class ChokaQSignalRNotifier : IChokaQNotifier
{
    // Use IHubContext to broadcast messages outside of a specific hub connection
    private readonly IHubContext<ChokaQHub> _hubContext;

    public ChokaQSignalRNotifier(IHubContext<ChokaQHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyJobUpdatedAsync(
        string jobId,
        string type,
        JobUIStatus status,
        int attemptCount,
        double? executionDurationMs = null,
        string? createdBy = null,
        DateTime? startedAtUtc = null,
        string queue = "default",
        int priority = 10)
    {

        await _hubContext.Clients.All.SendAsync("JobUpdated", new
        {
            JobId = jobId,
            Type = type,
            Status = status, // Serialized as int (0-5) or string depending on JSON config
            AttemptCount = attemptCount,
            ExecutionDurationMs = executionDurationMs,
            CreatedBy = createdBy,
            StartedAtUtc = startedAtUtc,
            Queue = queue,
            Priority = priority
        });
    }

    public Task NotifyJobProgressAsync(string jobId, int percentage)
    {
        return _hubContext.Clients.All.SendAsync("JobProgress", new { JobId = jobId, Percentage = percentage });
    }
}