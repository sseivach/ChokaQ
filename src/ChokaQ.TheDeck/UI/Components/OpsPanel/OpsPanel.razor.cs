using ChokaQ.Abstractions.DTOs;
using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel;

/// <summary>
/// The orchestrator for the side panel. 
/// Manages tab navigation, state retention, and event bubbling between child components and the main page.
/// </summary>
public partial class OpsPanel
{


    /// <summary>
    /// Indicates if the application is currently in History Mode (orange state).
    /// Used to determine if the "History Filter" tab should be shown/active.
    /// </summary>
    [Parameter] public bool IsHistoryMode { get; set; }


    [Parameter] public JobSource CurrentContext { get; set; } = JobSource.Archive;
    [Parameter] public int TotalItems { get; set; }
    [Parameter] public int CurrentPage { get; set; } = 1;
    [Parameter] public int PageSize { get; set; } = 100;
    [Parameter] public HubConnection? HubConnection { get; set; }



    /// <summary>
    /// Fired when the user requests to requeue (retry) a job.
    /// </summary>
    [Parameter] public EventCallback<string> OnRequeue { get; set; }

    /// <summary>
    /// Fired when the user requests to delete a job (or purge from DLQ).
    /// </summary>
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    /// <summary>
    /// Fired when the History Filter component requests data loading.
    /// The parent (TheDeck) will handle the actual data fetching.
    /// </summary>
    [Parameter] public EventCallback<HistoryFilterDto> OnLoadRequest { get; set; }

    /// <summary>
    /// Fired after an editor command changes storage state.
    /// The side panel can reload the inspector on its own, but the page shell owns counters,
    /// health indicators, and the current table. Bubbling this event keeps those surfaces
    /// reconciled from storage instead of letting the editor create a local UI-only truth.
    /// </summary>
    [Parameter] public EventCallback OnDataChanged { get; set; }



    private OpsTab _activeTab = OpsTab.None;
    private string? _selectedJobId;
    private JobEditorModel? _editorModel;

    /// <summary>
    /// Reacts to parameter changes from the parent.
    /// Handles automatic tab switching when entering/exiting History Mode.
    /// </summary>
    protected override void OnParametersSet()
    {

        if (IsHistoryMode && _activeTab == OpsTab.None)
        {
            _activeTab = OpsTab.HistoryFilter;
        }


        if (!IsHistoryMode && _activeTab == OpsTab.HistoryFilter)
        {
            _activeTab = OpsTab.None;
        }
    }



    /// <summary>
    /// Opens the Job Inspector for a specific job ID.
    /// Called when a user clicks a row in the main table.
    /// </summary>
    public void ShowJobInspector(string jobId)
    {
        _selectedJobId = jobId;
        _editorModel = null; // Clear any previous editor state
        SwitchTab(OpsTab.JobInspector);
    }

    /// <summary>
    /// Forces the panel to show the History Filter tab.
    /// Called when the user toggles the mode switch to "History".
    /// </summary>
    public void ShowHistoryFilter()
    {
        if (IsHistoryMode)
        {
            SwitchTab(OpsTab.HistoryFilter);
        }
    }

    /// <summary>
    /// Closes the panel or navigates back to the default view (Filters) depending on the mode.
    /// </summary>
    public void ClearPanel()
    {
        _selectedJobId = null;
        _editorModel = null;


        _activeTab = IsHistoryMode ? OpsTab.HistoryFilter : OpsTab.None;

        StateHasChanged();
    }



    private void SwitchTab(OpsTab tab)
    {
        _activeTab = tab;
        StateHasChanged();
    }



    /// <summary>
    /// Handles the "Edit" button click from the Inspector.
    /// Prepares the editor model and switches tabs.
    /// </summary>
    private void HandleEdit(JobEditorModel model)
    {
        _editorModel = model with { };
        SwitchTab(OpsTab.JobEditor);
    }

    /// <summary>
    /// Handles successful save from the Editor.
    /// Returns the user to the Inspector view.
    /// </summary>
    private async Task HandleJobSaved(string jobId)
    {
        ShowJobInspector(jobId);
        await OnDataChanged.InvokeAsync();
    }

    /// <summary>
    /// Handles the "Close" (X) button inside the Inspector.
    /// </summary>
    private void HandleInspectorClose()
    {
        ClearPanel();
    }

    /// <summary>
    /// Handles the "Cancel" button inside the Editor.
    /// Returns to the Inspector if a job was selected, otherwise closes.
    /// </summary>
    private void HandleEditorCancel()
    {
        if (_selectedJobId != null)
        {
            SwitchTab(OpsTab.JobInspector);
        }
        else
        {
            ClearPanel();
        }
    }


    private async Task HandleRequeue(string jobId) => await OnRequeue.InvokeAsync(jobId);

    private async Task HandleDelete(string jobId)
    {
        await OnDelete.InvokeAsync(jobId);

        ClearPanel();
    }

    /// <summary>
    /// Bubbles the load request from the HistoryFilter up to TheDeck.
    /// </summary>
    private async Task HandleLoadRequest(HistoryFilterDto filter)
    {
        await OnLoadRequest.InvokeAsync(filter);
    }
}
