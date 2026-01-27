using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Models;
using ChokaQ.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Features;

public partial class JobFeed : ComponentBase, IAsyncDisposable
{
    // In the new architecture, the component fetches data and handles navigation itself
    [Inject] private DashboardService DashboardService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    // Removed [Parameter] Jobs as the component loads them independently
    private List<JobViewModel> _jobs = new();

    // SignalR connection is now initialized internally (or injected if using a global provider)
    private HubConnection? _hubConnection;
    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    // --- State ---
    private bool _isInspectorVisible;

    // FIX: Changed from string ID to full Object to avoid DB lookups for Archived/Morgue jobs
    private JobViewModel? _selectedInspectorJob;

    // Filtering
    private string _searchQuery = "";
    // Switched to JobUIStatus (instead of the DB-specific JobStatus)
    private JobUIStatus? _activeStatusFilter = null;

    // Selection
    private HashSet<string> _selectedJobIds = new();
    private int SelectedCount => _selectedJobIds.Count;
    private int _bulkPriorityValue = 10;

    // Stats for the Card (Calculated locally for MVP)
    // NOTE: StatsCard.razor must accept Dictionary<JobUIStatus, int>
    private Dictionary<JobUIStatus, int> Counts => _jobs
        .GroupBy(j => j.Status)
        .ToDictionary(g => g.Key, g => g.Count());

    private bool IsAllVisibleSelected => FilteredJobs.Any() && FilteredJobs.All(j => _selectedJobIds.Contains(j.Id));

    // --- Computed Filter Logic ---
    private List<JobViewModel> FilteredJobs
    {
        get
        {
            IEnumerable<JobViewModel> query = _jobs;

            // 1. Filter by Status (UI Enum)
            // Note: Even if we load specific data from DB, keeping this ensures consistency
            if (_activeStatusFilter.HasValue)
            {
                query = query.Where(x => x.Status == _activeStatusFilter.Value);
            }

            // 2. Filter by Search Query
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                var term = _searchQuery.Trim();
                query = query.Where(x =>
                    x.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    x.Type.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    x.Queue.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (x.CreatedBy != null && x.CreatedBy.Contains(term, StringComparison.OrdinalIgnoreCase))
                );
            }

            // Sort: Newest first
            return query.OrderByDescending(x => x.CreatedAt ?? x.FinishedAt).ToList();
        }
    }

    // --- LIFECYCLE ---

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();
        await InitSignalRAsync();
    }

    private async Task LoadDataAsync()
    {
        // SMART LOAD LOGIC:
        // Switch the data source based on the selected filter.
        // If the user wants "Succeeded", we must fetch from History, not Active.

        if (_activeStatusFilter == JobUIStatus.Succeeded)
        {
            _jobs = await DashboardService.GetHistoryJobsAsync("default", 0, 100);
        }
        else if (_activeStatusFilter == JobUIStatus.Morgue)
        {
            _jobs = await DashboardService.GetMorgueJobsAsync("default", 0, 100);
        }
        else
        {
            // Default: Active (Pending/Processing/Retrying)
            _jobs = await DashboardService.GetActiveJobsAsync("default", 0, 100);
        }

        // Reset selection when data is reloaded to avoid ghost IDs
        _selectedJobIds.Clear();
        StateHasChanged();
    }

    private async Task InitSignalRAsync()
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/chokaq/hub"))
            .WithAutomaticReconnect()
            .Build();

        // LISTENER ONLY: We only listen for updates; we do not invoke Hub methods directly.
        _hubConnection.On<JobViewModel>("JobUpdated", (update) =>
        {
            InvokeAsync(() =>
            {
                HandleRealtimeUpdate(update);
                StateHasChanged();
            });
        });

        try { await _hubConnection.StartAsync(); }
        catch { /* Log error or handle offline state */ }
    }

    // --- REALTIME LOGIC ---
    private void HandleRealtimeUpdate(JobViewModel update)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == update.Id);
        if (existing != null)
        {
            // Update existing job properties
            existing.Status = update.Status;
            existing.DurationMs = update.DurationMs;
            existing.FinishedAt = update.FinishedAt;
            existing.AttemptCount = update.AttemptCount;
            existing.Payload = update.Payload; // Don't forget payload updates
        }
        else
        {
            // Add new job logic
            // Only add if it matches the current view context
            bool isHistoryView = _activeStatusFilter == JobUIStatus.Succeeded;
            bool isMorgueView = _activeStatusFilter == JobUIStatus.Morgue;

            if (!isHistoryView && !isMorgueView && (update.Status == JobUIStatus.Pending || update.Status == JobUIStatus.Processing))
            {
                _jobs.Insert(0, update);
            }
        }
    }

    // --- INTERACTION HANDLERS ---

    private void SetStatusFilter(JobUIStatus? status)
    {
        // Toggle logic
        if (_activeStatusFilter == status)
            _activeStatusFilter = null;
        else
            _activeStatusFilter = status;

        _selectedJobIds.Clear();

        // RELOAD REQUIRED: If we switch from "Pending" to "Succeeded", 
        // we must hit the DB to get the archive table.
        _ = LoadDataAsync();
    }

    private void ToggleSelection(string jobId, bool isSelected)
    {
        if (isSelected) _selectedJobIds.Add(jobId);
        else _selectedJobIds.Remove(jobId);
    }

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        var isChecked = (bool)(e.Value ?? false);
        if (isChecked)
        {
            foreach (var job in FilteredJobs) _selectedJobIds.Add(job.Id);
        }
        else
        {
            _selectedJobIds.Clear();
        }
    }

    private void ClearSelection() => _selectedJobIds.Clear();

    // "Clear History" usually implies truncating the archive. 
    // For now, we simply reload the data.
    private async Task OnClearHistory() => await LoadDataAsync();

    // --- ACTIONS (Service Calls instead of Hub Invokes) ---

    private async Task RestartSelected()
    {
        var toProcess = _selectedJobIds.ToList();
        foreach (var id in toProcess)
        {
            await DashboardService.ResurrectAsync(id);
        }

        // Optimistic update: Reload data to reflect changes
        await LoadDataAsync();
    }

    private async Task CancelSelected()
    {
        var toProcess = _selectedJobIds.ToList();
        foreach (var id in toProcess)
        {
            await DashboardService.PurgeAsync(id);
        }
        // Remove locally to avoid waiting for reload
        _jobs.RemoveAll(j => _selectedJobIds.Contains(j.Id));
        _selectedJobIds.Clear();
    }

    private Task SetPrioritySelected()
    {
        // TODO: Add SetPriority method to DashboardService if needed
        return Task.CompletedTask;
    }

    // --- SINGLE ACTIONS ---

    private async Task HandleCancelRequest(string jobId) => await HandleDeleteRequest(jobId);

    private async Task HandleRestartRequest(string jobId)
    {
        await DashboardService.ResurrectAsync(jobId);
        // Refresh data
        await LoadDataAsync();
    }

    private async Task HandleDeleteRequest(string jobId)
    {
        await DashboardService.PurgeAsync(jobId);
        _jobs.RemoveAll(j => j.Id == jobId);

        // Clean up inspector if deleted job was selected
        if (_selectedInspectorJob?.Id == jobId)
        {
            _isInspectorVisible = false;
            _selectedInspectorJob = null;
        }
    }

    // FIX: Accepting the full Object now
    private void OpenInspector(JobViewModel job)
    {
        _selectedInspectorJob = job;
        _isInspectorVisible = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}