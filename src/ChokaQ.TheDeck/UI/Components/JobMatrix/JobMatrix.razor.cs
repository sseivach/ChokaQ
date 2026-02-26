using ChokaQ.Abstractions.Enums;
using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.JobMatrix;

/// <summary>
/// Pure presentation component for the job table.
/// All actions (single and bulk) are raised as EventCallbacks to the parent orchestrator.
/// </summary>
public partial class JobMatrix
{
    [Parameter] public List<JobViewModel> Jobs { get; set; } = new();
    [Parameter] public bool IsConnected { get; set; }
    [Parameter] public EventCallback OnClearHistory { get; set; }
    [Parameter] public EventCallback<(DateTime?, DateTime?)> OnLoadHistory { get; set; }
    [Parameter] public JobStatus? ActiveStatusFilter { get; set; }
    [Parameter] public EventCallback<string> OnJobSelected { get; set; }
    [Parameter] public JobSource CurrentSource { get; set; } = JobSource.Hot;
    [Parameter] public EventCallback<JobSource> OnSourceChanged { get; set; }

    /// <summary>
    /// Event fired when an action is performed,
    /// signaling the parent to reload data (crucial for History Mode).
    /// </summary>
    [Parameter] public EventCallback OnRefreshRequested { get; set; }

    // --- Single-Row Action Callbacks ---

    /// <summary>Raised when user clicks Cancel on a single row.</summary>
    [Parameter] public EventCallback<string> OnCancel { get; set; }

    /// <summary>Raised when user clicks Restart on a single row.</summary>
    [Parameter] public EventCallback<string> OnRestart { get; set; }

    // --- Bulk Action Callbacks ---

    /// <summary>Raised when user clicks bulk Cancel for selected jobs.</summary>
    [Parameter] public EventCallback<HashSet<string>> OnBulkCancel { get; set; }

    /// <summary>Raised when user clicks bulk Retry for selected jobs.</summary>
    [Parameter] public EventCallback<HashSet<string>> OnBulkRetry { get; set; }

    /// <summary>Raised when user clicks bulk Purge for selected DLQ jobs.</summary>
    [Parameter] public EventCallback<HashSet<string>> OnBulkPurge { get; set; }

    private string _searchQuery = "";
    private DateTime? _dateFrom;
    private DateTime? _dateTo;
    private HashSet<string> _selectedJobIds = new();
    private int SelectedCount => _selectedJobIds.Count;
    private int _bulkPriorityValue = 10;
    private bool IsHistoryMode => CurrentSource != JobSource.Hot;

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

    public void ClearSelection() => _selectedJobIds.Clear();

    private async Task HandleJobSelected(string jobId)
    {
        await OnJobSelected.InvokeAsync(jobId);
    }

    // --- Single-Row Actions (delegate to parent) ---

    private async Task HandleCancelRequest(string jobId)
    {
        await OnCancel.InvokeAsync(jobId);
    }

    private async Task HandleRestartRequest(string jobId)
    {
        await OnRestart.InvokeAsync(jobId);
    }

    // --- Bulk Actions (delegate to parent) ---

    private async Task CancelSelected()
    {
        if (_selectedJobIds.Count == 0) return;
        await OnBulkCancel.InvokeAsync(new HashSet<string>(_selectedJobIds));
        _selectedJobIds.Clear();
    }

    private async Task RestartSelected()
    {
        if (_selectedJobIds.Count == 0) return;
        await OnBulkRetry.InvokeAsync(new HashSet<string>(_selectedJobIds));
        _selectedJobIds.Clear();
    }

    private async Task PurgeSelected()
    {
        if (_selectedJobIds.Count == 0) return;
        await OnBulkPurge.InvokeAsync(new HashSet<string>(_selectedJobIds));
        _selectedJobIds.Clear();
    }

    private async Task ChangeSource(JobSource source)
    {
        if (CurrentSource != source)
        {
            await OnSourceChanged.InvokeAsync(source);
        }
    }
}
