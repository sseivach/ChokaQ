using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using System.Timers;

namespace ChokaQ.TheDeck.UI.Pages;

public partial class TheDeck : IAsyncDisposable
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ChokaQTheDeckOptions Options { get; set; } = default!;
    [Inject] private IJobStorage JobStorage { get; set; } = default!;
    [Inject] private ICircuitBreaker CircuitBreaker { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private StatsSummaryEntity _counts = new(null, 0, 0, 0, 0, 0, 0, 0, null);
    private List<CircuitStatsDto> _circuits = new();
    private List<LogEntry> _logs = new();
    
    private Components.OpsPanel.OpsPanel _opsPanel = default!;
    private System.Timers.Timer? _uiRefreshTimer;
    private JobStatus? _activeStatusFilter;
    
    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    private const int MaxHistoryCount = 1000;

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

        RegisterHubHandlers();

        await _hubConnection.StartAsync();
        AddLog("System connected", "Success");
    }

    private void RegisterHubHandlers()
    {
        if (_hubConnection == null) return;

        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            InvokeAsync(() =>
            {
                var existing = _jobs.FirstOrDefault(j => j.Id == dto.JobId);
                if (existing != null)
                {
                    existing.Status = dto.Status;
                    existing.Attempts = dto.AttemptCount;
                    existing.Duration = dto.DurationMs.HasValue ? TimeSpan.FromMilliseconds(dto.DurationMs.Value) : null;
                    existing.StartedAtUtc = dto.StartedAtUtc?.ToLocalTime();
                    existing.Queue = dto.Queue;
                    existing.Priority = dto.Priority;
                    
                    if (existing.Status == JobStatus.Failed || existing.Status == JobStatus.Succeeded)
                    {
                        AddLog($"Job {dto.JobId.Substring(0,8)} {dto.Status}", dto.Status == JobStatus.Failed ? "Error" : "Success");
                    }
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
                    
                    AddLog($"Job {dto.JobId.Substring(0,8)} enqueued", "Info");

                    if (_jobs.Count > MaxHistoryCount) _jobs.RemoveAt(_jobs.Count - 1);
                }
                StateHasChanged();
            });
        });

        _hubConnection.On<string, string>("JobArchived", (jobId, queue) =>
        {
             InvokeAsync(() =>
             {
                 var job = _jobs.FirstOrDefault(j => j.Id == jobId);
                 if (job != null) job.Status = JobStatus.Succeeded;
                 StateHasChanged();
             });
        });

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
                 AddLog($"Job {jobId.Substring(0,8)} failed: {reason}", "Error");
                 StateHasChanged();
             });
        });

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
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _counts = await JobStorage.GetSummaryStatsAsync();
            _circuits = CircuitBreaker.GetCircuitStats().ToList();
            
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
                Duration = job.StartedAtUtc.HasValue ? DateTime.UtcNow - job.StartedAtUtc.Value : null,
                CreatedBy = job.CreatedBy,
                StartedAtUtc = job.StartedAtUtc?.ToLocalTime(),
                Payload = job.Payload ?? "{}"
            }).ToList();

            await InvokeAsync(() =>
            {
                 _jobs = viewModels;
                 StateHasChanged();
            });
        }
        catch { }
    }

    private void HandleStatusSelected(JobStatus? status)
    {
        _activeStatusFilter = status;
        StateHasChanged();
    }
    
    private void HandleJobInspectorRequested(string jobId)
    {
        _opsPanel.ShowJobInspector(jobId);
    }
    
    private async Task HandleRequeueRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("RestartJob", jobId);
    }

    private async Task HandleDeleteRequested(string jobId)
    {
        _opsPanel.ClearPanel();
    }

    private void ClearHistory()
    {
        _jobs.Clear();
        StateHasChanged();
    }
    
    private void AddLog(string message, string level)
    {
        _logs.Add(new LogEntry(DateTime.Now, message, level));
        if (_logs.Count > 500) _logs.RemoveAt(0);
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
