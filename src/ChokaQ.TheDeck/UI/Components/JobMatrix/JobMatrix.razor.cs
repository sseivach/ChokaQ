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
    private const int TypedConfirmationThreshold = 25;

    private enum BulkActionKind
    {
        Retry,
        Cancel,
        Purge
    }

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
    private HashSet<string> _selectedJobIds = new();
    private BulkActionKind? _pendingBulkAction;
    private string _bulkConfirmationText = "";
    private bool _bulkActionExecuting;

    private int SelectedCount => _selectedJobIds.Count;
    private bool IsHistoryMode => CurrentSource != JobSource.Hot;
    private string PendingBulkToken => _pendingBulkAction?.ToString().ToUpperInvariant() ?? "";
    private bool RequiresTypedConfirmation =>
        _pendingBulkAction == BulkActionKind.Purge || SelectedCount >= TypedConfirmationThreshold;
    private bool CanConfirmBulkAction =>
        _pendingBulkAction.HasValue
        && SelectedCount > 0
        && !_bulkActionExecuting
        && (!RequiresTypedConfirmation ||
            string.Equals(_bulkConfirmationText, PendingBulkToken, StringComparison.Ordinal));
    private string PendingBulkDescription => _pendingBulkAction switch
    {
        BulkActionKind.Retry => $"Retry {SelectedCount} selected jobs",
        BulkActionKind.Cancel => $"Cancel {SelectedCount} selected jobs",
        BulkActionKind.Purge => $"Permanently purge {SelectedCount} DLQ jobs",
        _ => ""
    };

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

        ResetPendingBulkAction();
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

        ResetPendingBulkAction();
    }

    public void ClearSelection()
    {
        _selectedJobIds.Clear();
        ResetPendingBulkAction();
    }

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

    private void RequestBulkRetry() => RequestBulkAction(BulkActionKind.Retry);

    private void RequestBulkCancel() => RequestBulkAction(BulkActionKind.Cancel);

    private void RequestBulkPurge() => RequestBulkAction(BulkActionKind.Purge);

    private void RequestBulkAction(BulkActionKind action)
    {
        if (_selectedJobIds.Count == 0) return;

        // Selected-row bulk actions are high-impact because the user can select hundreds of jobs
        // through virtualization. Arming the action first gives operators one explicit review step
        // before the parent component sends cancel/retry/purge commands to the Hub.
        _pendingBulkAction = action;
        _bulkConfirmationText = "";
    }

    private void ResetPendingBulkAction()
    {
        _pendingBulkAction = null;
        _bulkConfirmationText = "";
    }

    private async Task ConfirmBulkAction()
    {
        if (!CanConfirmBulkAction || _pendingBulkAction is null) return;

        var ids = new HashSet<string>(_selectedJobIds);
        _bulkActionExecuting = true;

        try
        {
            switch (_pendingBulkAction.Value)
            {
                case BulkActionKind.Retry:
                    await OnBulkRetry.InvokeAsync(ids);
                    break;
                case BulkActionKind.Cancel:
                    await OnBulkCancel.InvokeAsync(ids);
                    break;
                case BulkActionKind.Purge:
                    await OnBulkPurge.InvokeAsync(ids);
                    break;
            }

            _selectedJobIds.Clear();
            ResetPendingBulkAction();
        }
        finally
        {
            _bulkActionExecuting = false;
        }
    }

    private async Task ChangeSource(JobSource source)
    {
        if (CurrentSource != source)
        {
            ClearSelection();
            await OnSourceChanged.InvokeAsync(source);
        }
    }
}
