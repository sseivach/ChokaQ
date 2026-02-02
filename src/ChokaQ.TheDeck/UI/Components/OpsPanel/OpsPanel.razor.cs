using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel;

public partial class OpsPanel
{
    private OpsPanelType _activePanel = OpsPanelType.None;
    private string? _selectedJobId;

    [Parameter] public EventCallback<string> OnRequeue { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    public void ShowJobInspector(string jobId)
    {
        _activePanel = OpsPanelType.JobInspector;
        _selectedJobId = jobId;
        StateHasChanged();
    }

    public void ClearPanel()
    {
        _activePanel = OpsPanelType.None;
        _selectedJobId = null;
        StateHasChanged();
    }

    private async Task HandleRequeue(string jobId)
    {
        await OnRequeue.InvokeAsync(jobId);
    }

    private async Task HandleDelete(string jobId)
    {
        await OnDelete.InvokeAsync(jobId);
        ClearPanel();
    }

    private enum OpsPanelType { None, JobInspector }
}
