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

    private DateTime _lastStatsUpdate = DateTime.MinValue;
    private static readonly TimeSpan StatsUpdateThrottle = TimeSpan.FromMilliseconds(1000);
    private bool _renderPending = false;

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
            if (DateTime.UtcNow - _lastStatsUpdate < StatsUpdateThrottle) return;
            
            _lastStatsUpdate = DateTime.UtcNow;
            InvokeAsync(async () => 
            {
                _counts = await JobStorage.GetSummaryStatsAsync(); 
                ThrottledRender();
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
                     ThrottledRender();
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

            if (_activeStatusFilter == JobStatus.Succeeded ||
                _activeStatusFilter == JobStatus.Failed ||
                _activeStatusFilter == JobStatus.Cancelled)
            {
                // just refresh counters when viewing history
                await InvokeAsync(StateHasChanged);
                return;
            }

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

    private async Task HandleStatusSelected(JobStatus? status)
    {
        _activeStatusFilter = status;

        if (status == JobStatus.Failed || status == JobStatus.Succeeded || status == JobStatus.Cancelled)
        {
            await LoadHistoryAsync((null, null));
        }
        else
        {
            await LoadDataAsync();
        }

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

    private async Task LoadHistoryAsync((DateTime?, DateTime?) dateRange)
    {
        var (fromDate, toDate) = dateRange;
        var newJobs = new List<JobViewModel>();

        try
        {
            // Succeeded
            if (!_activeStatusFilter.HasValue || _activeStatusFilter == JobStatus.Succeeded)
            {
                var archiveJobs = await JobStorage.GetArchiveJobsAsync(
                    limit: MaxHistoryCount,
                    fromDate: fromDate,
                    toDate: toDate);

                newJobs.AddRange(archiveJobs.Select(j => new JobViewModel
                {
                    Id = j.Id,
                    Queue = j.Queue,
                    Type = j.Type,
                    Status = JobStatus.Succeeded,
                    Priority = 0,
                    Attempts = j.AttemptCount,
                    AddedAt = j.CreatedAtUtc.ToLocalTime(),
                    Duration = j.DurationMs.HasValue ? TimeSpan.FromMilliseconds(j.DurationMs.Value) : null,
                    CreatedBy = j.CreatedBy,
                    StartedAtUtc = j.StartedAtUtc?.ToLocalTime(),
                    Payload = j.Payload ?? "{}"
                }));
            }

            // Failed/Cancelled
            if (!_activeStatusFilter.HasValue ||
                 _activeStatusFilter == JobStatus.Failed ||
                 _activeStatusFilter == JobStatus.Cancelled)
            {
                var dlqJobs = await JobStorage.GetDLQJobsAsync(
                    limit: MaxHistoryCount,
                    fromDate: fromDate,
                    toDate: toDate);

                newJobs.AddRange(dlqJobs.Select(j => new JobViewModel
                {
                    Id = j.Id,
                    Queue = j.Queue,
                    Type = j.Type,
                    Status = JobStatus.Failed, // or Cancelled
                    Priority = 0,
                    Attempts = j.AttemptCount,
                    AddedAt = j.CreatedAtUtc.ToLocalTime(),
                    Duration = null,
                    CreatedBy = j.CreatedBy,
                    StartedAtUtc = null,
                    Payload = j.Payload ?? "{}"
                }));
            }

            var sorted = newJobs.OrderByDescending(j => j.AddedAt).ToList();

            await InvokeAsync(() =>
            {
                _jobs = sorted;
                StateHasChanged();
            });

            AddLog($"Loaded {sorted.Count} historical jobs", "Info");
        }
        catch (Exception ex)
        {
            AddLog($"Error loading history: {ex.Message}", "Error");
        }
    }

    private void AddLog(string message, string level)
    {
        _logs.Add(new LogEntry(DateTime.Now, message, level));
        if (_logs.Count > 500) _logs.RemoveAt(0);
        ThrottledRender();
    }

    private void HandleLog((string Message, string Level) log)
    {
        AddLog(log.Message, log.Level);
    }

    private void ThrottledRender()
    {
        if (_renderPending) return;
        _renderPending = true;
        
        InvokeAsync(async () => 
        {
            await Task.Delay(100); // Wait for more updates to arrive
            _renderPending = false;
            StateHasChanged();
        });
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
