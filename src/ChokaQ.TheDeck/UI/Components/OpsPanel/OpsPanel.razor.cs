using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel;

public partial class OpsPanel
{
    private OpsPanelType _activePanel = OpsPanelType.None;
    private string? _selectedJobId;
    private JobEditor.JobEditor.JobEditorModel? _editorModel;

    [Parameter] public EventCallback<string> OnRequeue { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    public void ShowJobInspector(string jobId)
    {
        _activePanel = OpsPanelType.JobInspector;
        _selectedJobId = jobId;
        _editorModel = null;
        StateHasChanged();
    }

    public void ShowJobEditor(JobEditor.JobEditor.JobEditorModel model)
    {
        _activePanel = OpsPanelType.JobEditor;
        _editorModel = model;
        _selectedJobId = model.Id;
        StateHasChanged();
    }

    public void ClearPanel()
    {
        _activePanel = OpsPanelType.None;
        _selectedJobId = null;
        _editorModel = null;
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

    private void HandleEdit(JobInspector.JobInspector.JobEditorModel model)
    {
        var editorModel = new JobEditor.JobEditor.JobEditorModel
        {
            Id = model.Id,
            Queue = model.Queue,
            Type = model.Type,
            Payload = model.Payload,
            Priority = model.Priority,
            Source = model.Source
        };

        ShowJobEditor(editorModel);
    }

    private async Task HandleJobSaved(string jobId)
    {
        // Return to inspector view after successful save
        ShowJobInspector(jobId);
    }

    private enum OpsPanelType { None, JobInspector, JobEditor }
}
