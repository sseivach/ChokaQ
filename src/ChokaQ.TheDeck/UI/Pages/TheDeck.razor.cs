using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using ChokaQ.Abstractions.Storage;
using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Pages;

/// <summary>
/// Main orchestration page. Manages state between Live Mode (Hot) and History Modes (Archive/DLQ).
/// Acts as the single source of truth for the job list and global counters.
/// </summary>
public partial class TheDeck : IAsyncDisposable
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ChokaQTheDeckOptions Options { get; set; } = default!;
    [Inject] private IJobStorage JobStorage { get; set; } = default!;
    [Inject] private ICircuitBreaker CircuitBreaker { get; set; } = default!;

    // --- State ---
    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private StatsSummaryEntity _counts = new(null, 0, 0, 0, 0, 0, 0, 0, null);
    private List<CircuitStatsDto> _circuits = new();
    private List<LogEntry> _logs = new();
    private List<ToastModel> _activeToasts = new();

    private Components.OpsPanel.OpsPanel _opsPanel = default!;
    private System.Timers.Timer? _uiRefreshTimer;

    // --- Mode & Context ---
    private JobSource _currentContext = JobSource.Hot;
    private bool IsHistoryMode => _currentContext != JobSource.Hot;
    private JobStatus? _activeStatusFilter;

    // --- History State ---
    private int _historyTotalItems;
    private int _historyPageNumber = 1;
    private int _historyPageSize = 100;

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    private const int MaxLiveCount = 100;

    private DateTime _lastStatsUpdate = DateTime.MinValue;
    private static readonly TimeSpan StatsUpdateThrottle = TimeSpan.FromMilliseconds(1000);
    private bool _renderPending = false;
    private HistoryFilterDto? _lastHistoryFilter;

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

    private async Task LoadDataAsync()
    {
        try
        {
            _counts = await JobStorage.GetSummaryStatsAsync();
            _circuits = CircuitBreaker.GetCircuitStats().ToList();

            if (_currentContext != JobSource.Hot)
            {
                await InvokeAsync(StateHasChanged);
                return;
            }

            var hotJobs = await JobStorage.GetActiveJobsAsync(MaxLiveCount, statusFilter: _activeStatusFilter);
            var viewModels = MapHotJobs(hotJobs);

            await InvokeAsync(() =>
            {
                _jobs = viewModels;
                StateHasChanged();
            });
        }
        catch { }
    }

    private async Task HandleSourceChanged(JobSource source)
    {
        if (_currentContext == source) return;

        _currentContext = source;
        _jobs.Clear();
        _activeStatusFilter = null;

        _opsPanel.ClearPanel();

        if (source == JobSource.Hot)
        {
            await LoadDataAsync();
        }
        else
        {
            _opsPanel.ShowHistoryFilter();
            await LoadInitialHistory(source);
        }

        StateHasChanged();
    }

    private async Task LoadInitialHistory(JobSource source)
    {
        var filter = new HistoryFilterDto(
            FromUtc: null,
            ToUtc: null,
            SearchTerm: null,
            Queue: null,
            Status: null,
            PageNumber: 1,
            PageSize: 10,
            SortBy: "Date",
            SortDescending: true
        );

        await HandleHistoryLoadRequest(filter);
    }

    private async Task HandleHistoryLoadRequest(HistoryFilterDto filter)
    {
        _lastHistoryFilter = filter;

        try
        {
            List<JobViewModel> viewModels;

            if (_currentContext == JobSource.Archive)
            {
                var result = await JobStorage.GetArchivePagedAsync(filter);
                _historyTotalItems = result.TotalCount;
                viewModels = MapArchiveJobs(result.Items);
            }
            else
            {
                var result = await JobStorage.GetDLQPagedAsync(filter);
                _historyTotalItems = result.TotalCount;
                viewModels = MapDLQJobs(result.Items);
            }

            _historyPageNumber = filter.PageNumber;
            _historyPageSize = filter.PageSize;

            await InvokeAsync(() =>
            {
                _jobs = viewModels;
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            AddLog($"Error loading history: {ex.Message}", "Error");
        }
    }

    private async Task HandleDataRefreshRequest()
    {
        if (IsHistoryMode && _lastHistoryFilter != null)
        {
            await Task.Delay(100);
            await HandleHistoryLoadRequest(_lastHistoryFilter);
        }
        else if (!IsHistoryMode)
        {
            await LoadDataAsync();
        }
    }

    private async Task HandleStatusSelected(JobStatus? status)
    {
        if (_currentContext == JobSource.Hot)
        {
            _activeStatusFilter = status;
            await LoadDataAsync();
        }
    }

    private void HandleJobInspectorRequested(string jobId)
    {
        _opsPanel.ShowJobInspector(jobId);
    }

    private async Task HandleRequeueRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("ResurrectJob", jobId, null, null);
        AddLog($"Resurrect request sent for {jobId}", "Success");

        if (_counts != null && _counts.FailedTotal > 0)
        {
            _counts = _counts with { FailedTotal = _counts.FailedTotal - 1 };
        }

        _opsPanel.ClearPanel();
        await HandleDataRefreshRequest();
    }

    private async Task HandleDeleteRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("PurgeDLQ", new[] { jobId });
        _opsPanel.ClearPanel();
        AddLog($"Delete request sent for {jobId}", "Warning");

        await HandleDataRefreshRequest();
    }

    private async Task HandleSingleCancel(string jobId)
    {
        if (_hubConnection is null) return;
        await _hubConnection.InvokeAsync("CancelJob", jobId);
        AddLog($"Cancel request sent for {jobId}", "Warning");
        await HandleDataRefreshRequest();
    }

    private async Task HandleSingleRestart(string jobId)
    {
        if (_hubConnection is null) return;
        await _hubConnection.InvokeAsync("RestartJob", jobId);
        AddLog($"Restart request sent for {jobId}", "Success");
        await HandleDataRefreshRequest();
    }

    private async Task HandleBulkCancel(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("CancelJobs", jobIds.ToArray());
        AddLog($"Bulk cancel: {jobIds.Count} jobs", "Warning");
        await HandleDataRefreshRequest();
    }

    private async Task HandleBulkRetry(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("RestartJobs", jobIds.ToArray());
        AddLog($"Bulk retry: {jobIds.Count} jobs", "Success");

        if (_currentContext == JobSource.DLQ && _counts.FailedTotal > 0)
        {
            var decrease = Math.Min(jobIds.Count, (int)_counts.FailedTotal);
            _counts = _counts with { FailedTotal = _counts.FailedTotal - decrease };
        }

        await HandleDataRefreshRequest();
    }

    private async Task HandleBulkPurge(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("PurgeDLQ", jobIds.ToArray());
        AddLog($"Bulk purge: {jobIds.Count} jobs permanently deleted", "Error");

        if (_counts.FailedTotal > 0)
        {
            var decrease = Math.Min(jobIds.Count, (int)_counts.FailedTotal);
            _counts = _counts with { FailedTotal = _counts.FailedTotal - decrease };
        }

        await HandleDataRefreshRequest();
    }

    private List<JobViewModel> MapHotJobs(IEnumerable<JobHotEntity> entities)
    {
        return entities.Select(job => new JobViewModel
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
    }

    private List<JobViewModel> MapArchiveJobs(IEnumerable<JobArchiveEntity> entities)
    {
        return entities.Select(j => new JobViewModel
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
        }).ToList();
    }

    private List<JobViewModel> MapDLQJobs(IEnumerable<JobDLQEntity> entities)
    {
        return entities.Select(j => new JobViewModel
        {
            Id = j.Id,
            Queue = j.Queue,
            Type = j.Type,
            Status = JobStatus.Failed,
            Priority = 0,
            Attempts = j.AttemptCount,
            AddedAt = j.CreatedAtUtc.ToLocalTime(),
            Duration = null,
            CreatedBy = j.CreatedBy,
            StartedAtUtc = null,
            Payload = j.Payload ?? "{}",
            ErrorDetails = j.ErrorDetails
        }).ToList();
    }

    private void RegisterHubHandlers()
    {
        if (_hubConnection == null) return;

        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            if (IsHistoryMode) return;

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

                    if (_jobs.Count > MaxLiveCount) _jobs.RemoveAt(_jobs.Count - 1);
                }
                ThrottledRender();
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
    }

    // --- TOAST AND LOGGING LOGIC ---

    private void AddLog(string message, string level)
    {
        _logs.Add(new LogEntry(DateTime.Now, message, level));
        if (_logs.Count > 500) _logs.RemoveAt(0);

        // Fire off a toast silently in the background
        _ = ShowToastAsync(message, level);

        ThrottledRender();
    }

    private async Task ShowToastAsync(string message, string level)
    {
        var toast = new ToastModel(Guid.NewGuid(), message, level);
        _activeToasts.Add(toast);
        await InvokeAsync(StateHasChanged);

        // Auto-dismiss after 4 seconds
        await Task.Delay(4000);

        if (_activeToasts.Contains(toast))
        {
            _activeToasts.Remove(toast);
            await InvokeAsync(StateHasChanged);
        }
    }

    private void RemoveToast(Guid id)
    {
        var toast = _activeToasts.FirstOrDefault(t => t.Id == id);
        if (toast != null)
        {
            _activeToasts.Remove(toast);
            StateHasChanged();
        }
    }

    private void HandleLog((string Message, string Level) log) => AddLog(log.Message, log.Level);

    private void ThrottledRender()
    {
        if (_renderPending) return;
        _renderPending = true;

        InvokeAsync(async () =>
        {
            await Task.Delay(100);
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