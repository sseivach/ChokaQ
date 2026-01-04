using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.SampleApp.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChokaQ.SampleApp.Services;

public class SignalRNotifier : IChokaQNotifier
{
    private readonly IHubContext<ChokaQHub> _hubContext;
    private readonly ILogger<SignalRNotifier> _logger;

    public SignalRNotifier(IHubContext<ChokaQHub> hubContext, ILogger<SignalRNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyJobUpdatedAsync(string jobId, JobStatus status)
    {
        _logger.LogInformation("[SignalRNotifier] Sending update. Job: {JobId}, Status: {Status}", jobId, status);

        // Explicitly cast Enum to int to match the client-side signature
        await _hubContext.Clients.All.SendAsync("JobUpdated", jobId, (int)status);
    }
}