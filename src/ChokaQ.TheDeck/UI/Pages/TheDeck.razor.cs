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


    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();
    private StatsSummaryEntity _counts = new(null, 0, 0, 0, 0, 0, 0, 0, null);
    private SystemHealthDto? _systemHealth;
    private List<CircuitStatsDto> _circuits = new();
    private List<LogEntry> _logs = new();
    private List<ToastModel> _activeToasts = new();

    private Components.OpsPanel.OpsPanel _opsPanel = default!;
    private Components.JobMatrix.JobMatrix _jobMatrix = default!;
    private System.Timers.Timer? _uiRefreshTimer;


    private JobSource _currentContext = JobSource.Hot;
    private bool IsHistoryMode => _currentContext != JobSource.Hot;
    private JobStatus? _activeStatusFilter;


    private int _historyTotalItems;
    private int _historyPageNumber = 1;
    private int _historyPageSize = 100;

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    private const int MaxLiveCount = 100;
    private const int ErrorFamilyDisplayLength = 160;

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
        await ReconcileCurrentViewAsync(clearSelection: false, refreshHistoryPage: false);
    }

    private async Task LoadShellStateAsync()
    {
        _counts = await JobStorage.GetSummaryStatsAsync();
        _systemHealth = await JobStorage.GetSystemHealthAsync();
        _circuits = CircuitBreaker.GetCircuitStats().ToList();
    }

    private async Task ReconcileCurrentViewAsync(bool clearSelection, bool refreshHistoryPage = true)
    {
        try
        {
            await LoadShellStateAsync();

            if (IsHistoryMode && refreshHistoryPage && _lastHistoryFilter != null)
            {
                await LoadHistoryPageAsync(_lastHistoryFilter);
            }
            else if (!IsHistoryMode)
            {
                var hotJobs = await JobStorage.GetActiveJobsAsync(MaxLiveCount, statusFilter: _activeStatusFilter);
                _jobs = MapHotJobs(hotJobs);
            }

            if (clearSelection)
            {
                _jobMatrix?.ClearSelection();
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            await InvokeAsync(() =>
            {
                AddLog($"Data sync failed. Showing stale data. Reason: {ex.Message}", "Error");
                StateHasChanged();
            });
        }
    }

    private async Task ReconcileAfterMutationAsync(bool clearSelection = true)
    {
        // Destructive commands can be filtered by ownership guards, admin scope, and concurrent
        // worker progress. The dashboard therefore never patches counters locally after a mutation:
        // it waits for the command path to settle and then reloads the canonical state from storage.
        await Task.Delay(100);
        await ReconcileCurrentViewAsync(clearSelection);
    }

    private async Task HandleSourceChanged(JobSource source)
    {
        if (_currentContext == source) return;

        _currentContext = source;
        _jobs.Clear();
        _activeStatusFilter = null;
        _lastHistoryFilter = null;

        _opsPanel.ClearPanel();

        if (source == JobSource.Hot)
        {
            await LoadDataAsync();
        }
        else
        {
            _opsPanel.ShowHistoryFilter();
            await LoadInitialHistory();
        }

        StateHasChanged();
    }

    private async Task LoadInitialHistory()
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
        try
        {
            await LoadShellStateAsync();
            await LoadHistoryPageAsync(filter);

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            AddLog($"Error loading history: {ex.Message}", "Error");
        }
    }

    private async Task HandleTopErrorSelected(DlqErrorGroupDto error)
    {
        // Top Errors use the same two dimensions that make DLQ triage useful at scale:
        // a typed FailureReason for routing and a normalized error prefix for grouping.
        // We deliberately do not add a date range here; incidents often span midnight or
        // several deploy windows, and an operator click should answer "show me this family"
        // before asking them to narrow time.
        _currentContext = JobSource.DLQ;
        _activeStatusFilter = null;
        _jobs.Clear();
        _jobMatrix?.ClearSelection();
        _opsPanel?.ShowHistoryFilter(force: true);

        var filter = new HistoryFilterDto(
            FromUtc: null,
            ToUtc: null,
            SearchTerm: error.ErrorPrefix,
            Queue: null,
            Status: null,
            PageNumber: 1,
            PageSize: 100,
            SortBy: "Date",
            SortDescending: true,
            FailureReason: error.FailureReason);

        AddLog($"DLQ filtered by {error.FailureReason}: {TrimForLog(error.ErrorPrefix)}", "Info");
        await HandleHistoryLoadRequest(filter);
    }

    private async Task LoadHistoryPageAsync(HistoryFilterDto filter)
    {
        var normalizedFilter = DashboardHistoryPaging.Normalize(filter);
        var page = await QueryHistoryPageAsync(normalizedFilter);

        var clampedFilter = DashboardHistoryPaging.ClampToAvailablePage(normalizedFilter, page.TotalCount);
        if (clampedFilter.PageNumber != normalizedFilter.PageNumber)
        {
            // Bulk purge/requeue can remove the last row from the page the operator is viewing.
            // Instead of showing an empty page with stale pagination, clamp to the new last page
            // and read that page from storage as the canonical post-mutation view.
            normalizedFilter = clampedFilter;
            page = await QueryHistoryPageAsync(normalizedFilter);
        }

        _lastHistoryFilter = normalizedFilter;
        _historyTotalItems = page.TotalCount;
        _historyPageNumber = normalizedFilter.PageNumber;
        _historyPageSize = normalizedFilter.PageSize;
        _jobs = page.ViewModels;
    }

    private async Task<(int TotalCount, List<JobViewModel> ViewModels)> QueryHistoryPageAsync(HistoryFilterDto filter)
    {
        if (_currentContext == JobSource.Archive)
        {
            var result = await JobStorage.GetArchivePagedAsync(filter);
            return (result.TotalCount, MapArchiveJobs(result.Items));
        }

        var dlqResult = await JobStorage.GetDLQPagedAsync(filter);
        return (dlqResult.TotalCount, MapDLQJobs(dlqResult.Items));
    }

    private async Task HandleDataRefreshRequest()
    {
        await ReconcileCurrentViewAsync(clearSelection: false);
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

        _opsPanel.ClearPanel();
        await ReconcileAfterMutationAsync();
    }

    private async Task HandleDeleteRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("PurgeDLQ", new[] { jobId });
        _opsPanel.ClearPanel();
        AddLog($"Delete request sent for {jobId}", "Warning");

        await ReconcileAfterMutationAsync();
    }

    private async Task HandleSingleCancel(string jobId)
    {
        if (_hubConnection is null) return;
        await _hubConnection.InvokeAsync("CancelJob", jobId);
        AddLog($"Cancel request sent for {jobId}", "Warning");
        await ReconcileAfterMutationAsync();
    }

    private async Task HandleSingleRestart(string jobId)
    {
        if (_hubConnection is null) return;
        await _hubConnection.InvokeAsync("RestartJob", jobId);
        AddLog($"Restart request sent for {jobId}", "Success");
        await ReconcileAfterMutationAsync();
    }

    private async Task HandleBulkCancel(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("CancelJobs", jobIds.ToArray());
        AddLog($"Bulk cancel: {jobIds.Count} jobs", "Warning");
        await ReconcileAfterMutationAsync();
    }

    private async Task HandleBulkRetry(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("RestartJobs", jobIds.ToArray());
        AddLog($"Bulk retry: {jobIds.Count} jobs", "Success");

        await ReconcileAfterMutationAsync();
    }

    private async Task HandleBulkPurge(HashSet<string> jobIds)
    {
        if (_hubConnection is null || jobIds.Count == 0) return;
        await _hubConnection.InvokeAsync("PurgeDLQ", jobIds.ToArray());
        AddLog($"Bulk purge: {jobIds.Count} jobs permanently deleted", "Error");

        await ReconcileAfterMutationAsync();
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
            ErrorDetails = j.ErrorDetails,
            ErrorFamily = NormalizeErrorFamily(j.ErrorDetails),
            FailureReason = j.FailureReason
        }).ToList();
    }

    private static string? NormalizeErrorFamily(string? errorDetails)
    {
        if (string.IsNullOrWhiteSpace(errorDetails))
            return null;

        // This mirrors the storage-side Top Errors normalization for display only. Storage
        // remains authoritative for grouping; the UI copy exists so each DLQ row teaches the
        // operator why it belongs to the same repeated failure family.
        var singleLine = errorDetails
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        while (singleLine.Contains("  ", StringComparison.Ordinal))
        {
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return singleLine.Length <= ErrorFamilyDisplayLength
            ? singleLine
            : singleLine[..ErrorFamilyDisplayLength];
    }

    private static string TrimForLog(string value)
    {
        const int maxLength = 96;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private void RegisterHubHandlers()
    {
        if (_hubConnection == null) return;

        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            if (IsHistoryMode) return;

            InvokeAsync(() =>
            {
                if (_activeStatusFilter.HasValue && dto.Status != _activeStatusFilter.Value)
                {
                    // Live SignalR updates arrive independently from the active status filter.
                    // If a job moves out of the selected status, remove it from the local slice and
                    // let the next storage reconciliation fill the window with the correct rows.
                    _jobs.RemoveAll(j => j.Id == dto.JobId);
                    ThrottledRender();
                    return;
                }

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
                await LoadShellStateAsync();
                ThrottledRender();
            });
        });
    }



    private void AddLog(string message, string level)
    {
        _logs.Add(new LogEntry(DateTime.Now, message, level));
        if (_logs.Count > 500) _logs.RemoveAt(0);


        _ = ShowToastAsync(message, level);

        ThrottledRender();
    }

    private async Task ShowToastAsync(string message, string level)
    {
        var toast = new ToastModel(Guid.NewGuid(), message, level);
        _activeToasts.Add(toast);
        await InvokeAsync(StateHasChanged);


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
