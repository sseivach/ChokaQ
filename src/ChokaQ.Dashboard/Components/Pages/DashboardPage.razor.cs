using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Dashboard.Components.Features;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Pages;

/// <summary>
/// Main Dashboard page with Three Pillars data integration.
/// Shows Hot (active), Archive (succeeded), and DLQ (failed) jobs.
/// </summary>
public partial class DashboardPage : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public ChokaQDashboardOptions Options { get; set; } = default!;
    [Inject] public IJobStorage JobStorage { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private StatsSummaryEntity _counts = new(null, 0, 0, 0, 0, 0, 0, 0, null);

    private System.Timers.Timer? _uiRefreshTimer;
    private string _currentTheme = "office";
    private CircuitMonitor? _circuitMonitor;

    private const int MaxHistoryCount = 1000;

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
        await LoadDataAsync();

        _uiRefreshTimer = new System.Timers.Timer(2000);
        _uiRefreshTimer.AutoReset = true;
        _uiRefreshTimer.Elapsed += async (sender, e) => await LoadDataAsync();
        _uiRefreshTimer.Start();

        var hubPath = Options.RoutePrefix.TrimEnd('/') + "/hub";
        var hubUrl = Navigation.ToAbsoluteUri(hubPath);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Real-time job updates
        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            InvokeAsync(() =>
            {
                var existing = _jobs.FirstOrDefault(j => j.Id == dto.JobId);
                if (existing != null)
                {
                    existing.Status = dto.Status;
                    existing.Attempts = dto.AttemptCount;
                    existing.Duration = dto.DurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(dto.DurationMs.Value)
                        : null;
                    existing.StartedAtUtc = dto.StartedAtUtc?.ToLocalTime();
                    existing.Queue = dto.Queue;
                    existing.Priority = dto.Priority;
                }
                else
                {
                    _jobs.Insert(0, new JobViewModel
                    {
                        Id = dto.JobId,
                        Queue = dto.Queue,
                        Type = dto.Type,
                        Status = dto.Status,
                        Attempts = dto.AttemptCount,
                        Priority = dto.Priority,
                        AddedAt = DateTime.Now,
                        CreatedBy = dto.CreatedBy,
                        StartedAtUtc = dto.StartedAtUtc?.ToLocalTime()
                    });

                    if (_jobs.Count > MaxHistoryCount)
                        _jobs.RemoveAt(_jobs.Count - 1);
                }

                _circuitMonitor?.Refresh();
                StateHasChanged();
            });
        });

        // Job archived (removed from Hot → Archive)
        _hubConnection.On<string, string>("JobArchived", (jobId, queue) =>
        {
            InvokeAsync(() =>
            {
                var job = _jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.Status = JobStatus.Succeeded;
                }
                StateHasChanged();
            });
        });

        // Job failed (removed from Hot → DLQ)
        _hubConnection.On<string, string, string>("JobFailed", (jobId, queue, reason) =>
        {
            InvokeAsync(() =>
            {
                var job = _jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.Status = JobStatus.Failed;
                    job.ErrorDetails = reason;
                }
                StateHasChanged();
            });
        });

        // Stats updated - refresh counts
        _hubConnection.On("StatsUpdated", () =>
        {
            InvokeAsync(async () =>
            {
                _counts = await JobStorage.GetSummaryStatsAsync();
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

    private async Task LoadDataAsync()
    {
        try
        {
            // 1. Fetch Summary Stats (Fast - from StatsSummary + Hot counts)
            var counts = await JobStorage.GetSummaryStatsAsync();

            // 2. Fetch Active Jobs from Hot table
            var hotJobs = await JobStorage.GetActiveJobsAsync(MaxHistoryCount);
            var viewModels = hotJobs.Select(job => new JobViewModel
            {
                Id = job.Id,
                Queue = job.Queue,
                Type = job.Type,
                Status = job.Status,
                Priority = job.Priority,
                Attempts = job.AttemptCount,
                AddedAt = job.CreatedAtUtc.ToLocalTime(),
                Duration = job.StartedAtUtc.HasValue
                    ? DateTime.UtcNow - job.StartedAtUtc.Value
                    : null,
                CreatedBy = job.CreatedBy,
                StartedAtUtc = job.StartedAtUtc?.ToLocalTime(),
                Payload = job.Payload ?? "{}"
            }).ToList();

            await InvokeAsync(() =>
            {
                _counts = counts;
                _jobs = viewModels;
                StateHasChanged();
            });
        }
        catch { /* ignore connection errors */ }
    }

    private void HandleSettingsUpdated() => StateHasChanged();

    private void ClearHistory()
    {
        _jobs.Clear();
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
