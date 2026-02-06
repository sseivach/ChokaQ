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
/// Main orchestration page. Manages state between Live Mode (Real-time) and History Mode (Paged).
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

    private Components.OpsPanel.OpsPanel _opsPanel = default!;
    private System.Timers.Timer? _uiRefreshTimer;

    // --- Mode & Context ---
    private bool _isHistoryMode;
    private JobSource _currentContext = JobSource.Hot; // Active context (Hot, Archive, or DLQ)
    private JobStatus? _activeStatusFilter; // For filtering Live Hot jobs (e.g. only Pending)

    // --- History State (Data binding for OpsPanel) ---
    private int _historyTotalItems;
    private int _historyPageNumber = 1;
    private int _historyPageSize = 100;

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    private const int MaxLiveCount = 100; // Limit live rows to keep DOM light

    // Throttling for SignalR updates to prevent UI freezing
    private DateTime _lastStatsUpdate = DateTime.MinValue;
    private static readonly TimeSpan StatsUpdateThrottle = TimeSpan.FromMilliseconds(1000);
    private bool _renderPending = false;

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();

        // Start background timer for counters and live table updates
        // It runs every 2 seconds but respects the IsHistoryMode flag
        _uiRefreshTimer = new System.Timers.Timer(2000);
        _uiRefreshTimer.AutoReset = true;
        _uiRefreshTimer.Elapsed += async (sender, e) => await LoadDataAsync();
        _uiRefreshTimer.Start();

        // Initialize SignalR
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

    /// <summary>
    /// The "Smart Timer" Loop.
    /// Updates counters globally, but only updates the Job Table if in Live Mode.
    /// </summary>
    private async Task LoadDataAsync()
    {
        try
        {
            // 1. Always update global counters and circuit breakers (Fast & Cheap operation)
            _counts = await JobStorage.GetSummaryStatsAsync();
            _circuits = CircuitBreaker.GetCircuitStats().ToList();

            // 2. STOP: If in History Mode, DO NOT touch the job table.
            // In History Mode, the table is controlled manually by the OpsPanel filters.
            if (_isHistoryMode)
            {
                await InvokeAsync(StateHasChanged);
                return;
            }

            // 3. Live Mode: Load Active (Hot) Jobs
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

    /// <summary>
    /// Callback from OpsPanel -> HistoryFilter.
    /// Executed when user clicks "LOAD DATA" or changes page/sort.
    /// </summary>
    private async Task HandleHistoryLoadRequest(HistoryFilterDto filter)
    {
        try
        {
            List<JobViewModel> viewModels;

            if (_currentContext == JobSource.Archive)
            {
                // Fetch from Archive Table
                var result = await JobStorage.GetArchivePagedAsync(filter);
                _historyTotalItems = result.TotalCount;
                viewModels = MapArchiveJobs(result.Items);
            }
            else // DLQ
            {
                // Fetch from DLQ Table
                var result = await JobStorage.GetDLQPagedAsync(filter);
                _historyTotalItems = result.TotalCount;
                viewModels = MapDLQJobs(result.Items);
            }

            // Update local state to reflect current page in pagination controls
            _historyPageNumber = filter.PageNumber;
            _historyPageSize = filter.PageSize;

            await InvokeAsync(() =>
            {
                _jobs = viewModels;
                StateHasChanged();
            });

            AddLog($"Loaded {viewModels.Count} records from {_currentContext}", "Info");
        }
        catch (Exception ex)
        {
            AddLog($"Error loading history: {ex.Message}", "Error");
        }
    }

    /// <summary>
    /// Handles user clicking on top Stats Cards (Pending, Failed, etc.)
    /// Automatically switches context based on the card type.
    /// </summary>
    private async Task HandleStatusSelected(JobStatus? status)
    {
        // 1. Case: Failed / Succeeded -> Switch to History Mode automatically
        if (status == JobStatus.Failed || status == JobStatus.Cancelled)
        {
            _activeStatusFilter = status;
            await SwitchMode(true, JobSource.DLQ);

            // Auto-open filter panel so user sees where they are
            _opsPanel.ShowHistoryFilter();
        }
        else if (status == JobStatus.Succeeded)
        {
            _activeStatusFilter = status;
            await SwitchMode(true, JobSource.Archive);
            _opsPanel.ShowHistoryFilter();
        }
        else
        {
            // 2. Case: Pending / Processing / Total -> Switch to Live Mode
            _activeStatusFilter = status;
            await SwitchMode(false, JobSource.Hot);
        }
    }

    /// <summary>
    /// Handles the manual toggle switch [LIVE | HISTORY] in the toolbar.
    /// </summary>
    private async Task HandleModeToggle(bool isHistory)
    {
        // If switching to History manually, default to Archive unless we were already in DLQ.
        // If switching to Live, force Hot context.
        var targetContext = isHistory
            ? (_currentContext == JobSource.Hot ? JobSource.Archive : _currentContext)
            : JobSource.Hot;

        await SwitchMode(isHistory, targetContext);
    }

    private async Task SwitchMode(bool historyMode, JobSource context)
    {
        _isHistoryMode = historyMode;
        _currentContext = context;

        // Clear the table to indicate context switch
        _jobs.Clear();

        if (_isHistoryMode)
        {
            // Stop live updates, open filter panel
            _opsPanel.ShowHistoryFilter();
        }
        else
        {
            // Resume live updates, close history panel, trigger immediate refresh
            _opsPanel.ClearPanel();
            await LoadDataAsync();
        }

        StateHasChanged();
    }

    // --- Interaction Handlers (Actions) ---

    private void HandleJobInspectorRequested(string jobId)
    {
        _opsPanel.ShowJobInspector(jobId);
    }

    private async Task HandleRequeueRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("ResurrectJob", jobId, null, null);
        AddLog($"Resurrect request sent for {jobId}", "Info");
    }

    private async Task HandleDeleteRequested(string jobId)
    {
        if (_hubConnection != null) await _hubConnection.InvokeAsync("PurgeDLQ", new[] { jobId });
        _opsPanel.ClearPanel();

        // If we are in history view, ideally we should reload the page, 
        // but for now we let the user click Load again to refresh.
        AddLog($"Delete request sent for {jobId}", "Warning");
    }

    // --- Mappers (Entity -> ViewModel) ---

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

    // --- Logging & SignalR ---

    private void RegisterHubHandlers()
    {
        if (_hubConnection == null) return;

        // Listen for live updates only if we are in Live Mode
        _hubConnection.On<JobUpdateDto>("JobUpdated", (dto) =>
        {
            // Only update UI if we are in Live Mode
            if (_isHistoryMode) return;

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
                    // Add new job to the top
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

        // Stats updates are always relevant (counters)
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

    private void AddLog(string message, string level)
    {
        _logs.Add(new LogEntry(DateTime.Now, message, level));
        if (_logs.Count > 500) _logs.RemoveAt(0);
        ThrottledRender();
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