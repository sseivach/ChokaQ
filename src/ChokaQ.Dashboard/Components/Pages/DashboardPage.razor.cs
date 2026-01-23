using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Dashboard.Components.Features;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Pages;

public partial class DashboardPage : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public ChokaQDashboardOptions Options { get; set; } = default!;
    [Inject] public IJobStorage JobStorage { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private System.Timers.Timer? _uiRefreshTimer;
    private string _currentTheme = "office";
    private CircuitMonitor? _circuitMonitor;
    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    // --- Theme Logic ---
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
        // 1. Initial Data Load
        await LoadJobsAsync();

        // 2. Setup Polling
        _uiRefreshTimer = new System.Timers.Timer(2000);
        _uiRefreshTimer.AutoReset = true;
        _uiRefreshTimer.Elapsed += async (sender, e) => await LoadJobsAsync();
        _uiRefreshTimer.Start();

        // 3. SignalR Setup
        var hubPath = Options.RoutePrefix.TrimEnd('/') + "/hub";
        var hubUrl = Navigation.ToAbsoluteUri(hubPath);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // [FULL SIGNALR PIPELINE via DTO]
        // We now receive a single DTO object, fixing the "9 arguments" error.
        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            InvokeAsync(() =>
            {
                var existing = _jobs.FirstOrDefault(j => j.Id == dto.JobId);

                if (existing != null)
                {
                    // Update existing row
                    existing.Status = dto.Status;
                    existing.Attempts = dto.AttemptCount;
                    existing.Duration = dto.ExecutionDurationMs.HasValue
                        ? TimeSpan.FromMilliseconds(dto.ExecutionDurationMs.Value)
                        : null;
                    existing.StartedAtUtc = dto.StartedAtUtc?.ToLocalTime();

                    // Update metadata
                    existing.Queue = dto.Queue;
                    existing.Priority = dto.Priority;
                }
                else
                {
                    // New Job
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

                    if (_jobs.Count > 1000) _jobs.RemoveAt(_jobs.Count - 1);
                }

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

    private async Task LoadJobsAsync()
    {
        try
        {
            var storageJobs = await JobStorage.GetJobsAsync(1000);

            var viewModels = storageJobs.Select(dto => new JobViewModel
            {
                Id = dto.Id,
                Queue = dto.Queue,
                Type = dto.Type,
                Status = dto.Status,
                Priority = dto.Priority,
                Attempts = dto.AttemptCount,
                AddedAt = dto.CreatedAtUtc.ToLocalTime(),
                Duration = dto.FinishedAtUtc.HasValue && dto.StartedAtUtc.HasValue
                    ? dto.FinishedAtUtc.Value - dto.StartedAtUtc.Value
                    : null,
                CreatedBy = dto.CreatedBy,
                StartedAtUtc = dto.StartedAtUtc?.ToLocalTime(),
                Payload = dto.Payload,
                ErrorDetails = dto.ErrorDetails
            }).ToList();

            await InvokeAsync(() =>
            {
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