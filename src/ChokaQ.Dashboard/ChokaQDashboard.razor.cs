using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Components;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using System.Timers;

namespace ChokaQ.Dashboard;

public partial class ChokaQDashboard : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;

    private HubConnection? _hubConnection;

    // We keep a separate list for the UI execution to avoid "Collection Modified" errors during rendering
    private List<JobViewModel> _jobs = new();

    // Throttling timer
    private System.Timers.Timer? _uiRefreshTimer;
    private bool _dirty = false; // Flag to indicate if data changed

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
        _ => "cq-theme-office"
    };

    private void HandleThemeChanged(string newTheme)
    {
        _currentTheme = newTheme;
        StateHasChanged();
    }

    protected override async Task OnInitializedAsync()
    {
        // 1. Setup Throttling Timer (Updates UI every 500ms max)
        _uiRefreshTimer = new System.Timers.Timer(500);
        _uiRefreshTimer.AutoReset = true;
        _uiRefreshTimer.Elapsed += async (sender, e) =>
        {
            if (_dirty)
            {
                _dirty = false;
                await InvokeAsync(StateHasChanged);
            }
        };
        _uiRefreshTimer.Start();

        // 2. Setup SignalR
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/chokaq-hub"))
            .WithAutomaticReconnect()
            .Build();

        // Receive 'type' parameter
        _hubConnection.On<string, string, int, int>("JobUpdated", (jobId, type, statusInt, attempts) =>
        {
            var status = (JobStatus)statusInt;

            // We do NOT call StateHasChanged here. We just update data.
            InvokeAsync(() =>
            {
                UpdateJob(jobId, type, status, attempts);
                _circuitMonitor?.Refresh(); // Circuit monitor is light, can update often
                _dirty = true; // Mark for next refresh tick
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
                    _dirty = true;
                }
            });
        });

        await _hubConnection.StartAsync();
    }

    private void UpdateJob(string jobId, string type, JobStatus status, int attempts)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == jobId);
        var now = DateTime.Now;

        if (existing != null)
        {
            // Update existing
            existing.Status = status;
            existing.Attempts = attempts;
            existing.Type = type;

            if (status == JobStatus.Succeeded || status == JobStatus.Failed || status == JobStatus.Cancelled)
            {
                existing.Duration = now - existing.AddedAt;
            }
        }
        else
        {
            // Insert new (Top of the list)
            _jobs.Insert(0, new JobViewModel
            {
                Id = jobId,
                Type = type,
                Status = status,
                Attempts = attempts,
                AddedAt = now,
                Duration = TimeSpan.Zero
            });

            // Keep memory check - strictly cap at 1000 for UI safety
            if (_jobs.Count > 1000)
            {
                _jobs.RemoveRange(1000, _jobs.Count - 1000);
            }
        }
    }

    private void HandleSettingsUpdated()
    {
        StateHasChanged();
    }

    private void ClearHistory()
    {
        _jobs.Clear();
        _dirty = true;
        StateHasChanged(); // Force immediate clear
    }

    public async ValueTask DisposeAsync()
    {
        if (_uiRefreshTimer is not null)
        {
            _uiRefreshTimer.Stop();
            _uiRefreshTimer.Dispose();
        }

        if (_hubConnection is not null) await _hubConnection.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}