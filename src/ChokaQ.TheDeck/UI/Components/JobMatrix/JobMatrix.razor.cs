using ChokaQ.Abstractions.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Components.JobMatrix;

public partial class JobMatrix
{
    [Parameter] public List<JobViewModel> Jobs { get; set; } = new();
    [Parameter] public bool IsConnected { get; set; }
    [Parameter] public EventCallback OnClearHistory { get; set; }
    [Parameter] public HubConnection? HubConnection { get; set; }
    [Parameter] public JobStatus? ActiveStatusFilter { get; set; }
    [Parameter] public EventCallback<string> OnJobSelected { get; set; }

    private string _searchQuery = "";
    private HashSet<string> _selectedJobIds = new();
    private int SelectedCount => _selectedJobIds.Count;
    private int _bulkPriorityValue = 10;

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

    private void ClearSelection() => _selectedJobIds.Clear();

    private async Task HandleJobSelected(string jobId)
    {
        await OnJobSelected.InvokeAsync(jobId);
    }

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
}
