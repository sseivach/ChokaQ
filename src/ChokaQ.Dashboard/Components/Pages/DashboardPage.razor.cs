using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Components.Features;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using System.Timers;

namespace ChokaQ.Dashboard.Components.Pages;

public partial class DashboardPage : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public ChokaQDashboardOptions Options { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private System.Timers.Timer? _uiRefreshTimer;
    private bool _dirty = false;
    private string _currentTheme = "office";
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

        // --- FIXED: DYNAMIC HUB URL ---
        // We use the RoutePrefix from options to build the correct Hub URL.
        // If RoutePrefix is "/admin/jobs", Hub is "/admin/jobs/hub".

        var hubPath = Options.RoutePrefix.TrimEnd('/') + "/hub";
        var hubUrl = Navigation.ToAbsoluteUri(hubPath);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<string, string, int, int, double?, string?, DateTime?>("JobUpdated",
            (jobId, type, statusInt, attempts, durationMs, createdBy, startedAt) =>
            {
                var status = (JobStatus)statusInt;
                InvokeAsync(() =>
                {
                    UpdateJob(jobId, type, status, attempts, durationMs, createdBy, startedAt);
                    _circuitMonitor?.Refresh();
                    _dirty = true;
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

    private void UpdateJob(string jobId, string type, JobStatus status, int attempts, double? durationMs, string? createdBy, DateTime? startedAt)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == jobId);
        var now = DateTime.Now;

        if (existing != null)
        {
            existing.Status = status;
            existing.Attempts = attempts;
            existing.Type = type;
            if (createdBy != null) existing.CreatedBy = createdBy;
            if (startedAt != null) existing.StartedAtUtc = startedAt;
            if (durationMs.HasValue) existing.Duration = TimeSpan.FromMilliseconds(durationMs.Value);
        }
        else
        {
            _jobs.Insert(0, new JobViewModel
            {
                Id = jobId,
                Type = type,
                Status = status,
                Attempts = attempts,
                AddedAt = now,
                Duration = durationMs.HasValue ? TimeSpan.FromMilliseconds(durationMs.Value) : TimeSpan.Zero,
                CreatedBy = createdBy,
                StartedAtUtc = startedAt
            });

            if (_jobs.Count > 1000) _jobs.RemoveRange(1000, _jobs.Count - 1000);
        }
    }

    private void HandleSettingsUpdated() => StateHasChanged();

    private void ClearHistory()
    {
        _jobs.Clear();
        _dirty = true;
        StateHasChanged();
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