using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard;

public partial class ChokaQDashboard : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;

    // We don't need to inject WorkerManager here for the settings anymore, 
    // unless we want to initialize other things, but SettingsComponent handles its own injections.
    // However, we still might keep it if needed for other logic.

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    protected override async Task OnInitializedAsync()
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/chokaq-hub"))
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<string, int, int>("JobUpdated", (jobId, statusInt, attempts) =>
        {
            var status = (JobStatus)statusInt;
            InvokeAsync(() =>
            {
                UpdateJob(jobId, status, attempts);
                StateHasChanged();
            });
        });

        _hubConnection.On<string, int>("JobProgress", (jobId, percentage) =>
        {
            InvokeAsync(() =>
            {
                var job = _jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.Progress = percentage;
                    StateHasChanged();
                }
            });
        });

        await _hubConnection.StartAsync();
    }

    private void UpdateJob(string jobId, JobStatus status, int attempts)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == jobId);
        var now = DateTime.Now;

        if (existing != null)
        {
            existing.Status = status;
            existing.Attempts = attempts;

            if (status == JobStatus.Succeeded || status == JobStatus.Failed)
            {
                existing.Duration = now - existing.AddedAt;
            }
        }
        else
        {
            _jobs.Add(new JobViewModel
            {
                Id = jobId,
                Status = status,
                Attempts = attempts,
                AddedAt = now,
                Duration = TimeSpan.Zero
            });

            if (_jobs.Count > 100) _jobs.RemoveAt(0);
        }
    }

    private void HandleSettingsUpdated()
    {
        // Settings component handled the logic, we just refresh UI if needed
        StateHasChanged();
    }

    private void ClearHistory()
    {
        _jobs.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null) await _hubConnection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}