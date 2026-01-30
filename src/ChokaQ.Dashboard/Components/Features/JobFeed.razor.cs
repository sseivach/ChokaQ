using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard.Components.Features;

/// <summary>
/// Main job feed component displaying active jobs from the Hot table.
/// Supports filtering, searching, multi-select, and bulk operations.
/// </summary>
/// <remarks>
/// Features:
/// - Real-time updates via SignalR subscription
/// - Status filtering via StatsCard clicks
/// - Full-text search across Id, Type, Queue, CreatedBy
/// - Multi-select with bulk actions (Cancel, Restart, Priority)
/// - Virtualized rendering for large job lists (1000+ items)
/// </remarks>
public partial class JobFeed
{
    /// <summary>Jobs from Hot table, populated by parent DashboardPage.</summary>
    [Parameter] public List<JobViewModel> Jobs { get; set; } = new();

    /// <summary>Aggregated statistics for the StatsCard header.</summary>
    [Parameter] public StatsSummaryEntity Counts { get; set; } = new(null, 0, 0, 0, 0, 0, 0, 0, null);

    /// <summary>SignalR connection state for UI indicators.</summary>
    [Parameter] public bool IsConnected { get; set; }

    /// <summary>Callback to clear job history in parent component.</summary>
    [Parameter] public EventCallback OnClearHistory { get; set; }

    /// <summary>SignalR hub connection for sending commands to server.</summary>
    [Parameter] public HubConnection? HubConnection { get; set; }

    // Inspector panel state
    private bool _isInspectorVisible;
    private string? _selectedInspectorJobId;

    // Filtering state
    private string _searchQuery = "";

    /// <summary>Status filter applied by parent component.</summary>
    [Parameter] public JobStatus? ActiveStatusFilter { get; set; }

    // Multi-select state for bulk operations
    private HashSet<string> _selectedJobIds = new();
    private int SelectedCount => _selectedJobIds.Count;
    private int _bulkPriorityValue = 10;

    // Computed Filter Logic
    private ICollection<JobViewModel> FilteredJobs
    {
        get
        {
            IEnumerable<JobViewModel> query = Jobs;

            if (ActiveStatusFilter.HasValue)
            {
                query = query.Where(x => x.Status == ActiveStatusFilter.Value);
            }

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

            return query.ToList();
        }
    }

    protected override void OnParametersSet()
    {
        // Clear selection if the list of jobs might have changed significantly due to filtering
        // This is a simple heuristic; you might want more complex logic to retain selection if valid.
        // For now, consistent behavior compliant with "ruthless cleanup".
    }

    private void ToggleSelection(string jobId, bool isSelected)
    {
        if (isSelected) _selectedJobIds.Add(jobId);
        else _selectedJobIds.Remove(jobId);
    }

    private bool IsAllVisibleSelected => FilteredJobs.Any() && FilteredJobs.All(j => _selectedJobIds.Contains(j.Id));

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

    // --- ACTIONS ---

    private async Task RestartSelected()
    {
        if (HubConnection is not null && IsConnected)
        {
            var toProcess = _selectedJobIds.ToList();
            foreach (var id in toProcess) await HubConnection.InvokeAsync("RestartJob", id);
            _selectedJobIds.Clear();
        }
    }

    private async Task CancelSelected()
    {
        if (HubConnection is not null && IsConnected)
        {
            var toProcess = _selectedJobIds.ToList();
            foreach (var id in toProcess) await HubConnection.InvokeAsync("CancelJob", id);
            _selectedJobIds.Clear();
        }
    }

    private async Task SetPrioritySelected()
    {
        if (HubConnection is not null && IsConnected)
        {
            var toProcess = _selectedJobIds.ToList();
            foreach (var id in toProcess)
            {
                await HubConnection.InvokeAsync("SetPriority", id, _bulkPriorityValue);
            }
            _selectedJobIds.Clear();
        }
    }

    // --- SINGLE ACTIONS ---

    private async Task HandleCancelRequest(string jobId)
    {
        if (HubConnection is not null && IsConnected)
            await HubConnection.InvokeAsync("CancelJob", jobId);
    }

    private async Task HandleRestartRequest(string jobId)
    {
        if (HubConnection is not null && IsConnected)
            await HubConnection.InvokeAsync("RestartJob", jobId);
    }

    private void OpenInspector(string jobId)
    {
        _selectedInspectorJobId = jobId;
        _isInspectorVisible = true;
    }

    private Task HandleDeleteRequest(string jobId)
    {
        _isInspectorVisible = false;
        return Task.CompletedTask;
    }
}