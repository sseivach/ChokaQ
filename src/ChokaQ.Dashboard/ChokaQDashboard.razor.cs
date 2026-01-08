using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Components;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard;

public partial class ChokaQDashboard : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();

    // Default is "office"
    private string _currentTheme = "office";

    // Reference to the child component
    private CircuitMonitor? _circuitMonitor;

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    private string CurrentThemeClass => _currentTheme switch
    {
        "nightshift" => "cq-theme-nightshift",
        "caviar" => "cq-theme-caviar",
        "flashbang" => "cq-theme-flashbang",
        "kiddie" => "cq-theme-kiddie",
        "bravosix" => "cq-theme-bravosix",
        "bsod" => "cq-theme-bsod",

        _ => "cq-theme-office" // Default falls to Office
    };

    private void HandleThemeChanged(string newTheme)
    {
        _currentTheme = newTheme;
        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/chokaq-hub"))
            .WithAutomaticReconnect()
            .Build();

        // Receive 'type' parameter
        _hubConnection.On<string, string, int, int>("JobUpdated", (jobId, type, statusInt, attempts) =>
        {
            var status = (JobStatus)statusInt;
            InvokeAsync(() =>
            {
                // Pass 'type' to update logic
                UpdateJob(jobId, type, status, attempts);

                // Check circuit health whenever a job updates
                _circuitMonitor?.Refresh();

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

    // Added 'type' parameter
    private void UpdateJob(string jobId, string type, JobStatus status, int attempts)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == jobId);
        var now = DateTime.Now;

        if (existing != null)
        {
            existing.Status = status;
            existing.Attempts = attempts;
            existing.Type = type; // Update type (though typically constant)

            if (status == JobStatus.Succeeded || status == JobStatus.Failed || status == JobStatus.Cancelled)
            {
                existing.Duration = now - existing.AddedAt;
            }
        }
        else
        {
            _jobs.Add(new JobViewModel
            {
                Id = jobId,
                Type = type, // Set type
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