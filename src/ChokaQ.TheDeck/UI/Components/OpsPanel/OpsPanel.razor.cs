using ChokaQ.Abstractions.DTOs;
using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel;

/// <summary>
/// The orchestrator for the side panel. 
/// Manages tab navigation, state retention, and event bubbling between child components and the main page.
/// </summary>
public partial class OpsPanel
{
    // --- Parameters from Parent (TheDeck) ---

    /// <summary>
    /// Indicates if the application is currently in History Mode (orange state).
    /// Used to determine if the "History Filter" tab should be shown/active.
    /// </summary>
    [Parameter] public bool IsHistoryMode { get; set; }

    // --- Data Parameters for History Filter (Placeholders for Stage 3) ---
    // These are passed down from TheDeck so the filter component knows what to display.
    [Parameter] public JobSource CurrentContext { get; set; } = JobSource.Archive;
    [Parameter] public int TotalItems { get; set; }
    [Parameter] public int CurrentPage { get; set; } = 1;
    [Parameter] public int PageSize { get; set; } = 100;

    // --- Events ---

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

    // --- Internal State ---

    private OpsTab _activeTab = OpsTab.None;
    private string? _selectedJobId;
    private JobEditorModel? _editorModel;

    /// <summary>
    /// Reacts to parameter changes from the parent.
    /// Handles automatic tab switching when entering/exiting History Mode.
    /// </summary>
    protected override void OnParametersSet()
    {
        // Rule 1: If we switched TO History Mode and nothing is open, show the Filters.
        if (IsHistoryMode && _activeTab == OpsTab.None)
        {
            _activeTab = OpsTab.HistoryFilter;
        }

        // Rule 2: If we switched FROM History Mode (to Live) and Filters were open, close the panel.
        // We don't want to show history filters in Live mode.
        if (!IsHistoryMode && _activeTab == OpsTab.HistoryFilter)
        {
            _activeTab = OpsTab.None;
        }
    }

    // --- Public API (Called by TheDeck via @ref) ---

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

        // Logic: If in History Mode, "Closing" an inspector just takes us back to the Filter list.
        // If in Live Mode, "Closing" shuts the whole panel.
        _activeTab = IsHistoryMode ? OpsTab.HistoryFilter : OpsTab.None;

        StateHasChanged();
    }

    // --- Internal Logic ---

    private void SwitchTab(OpsTab tab)
    {
        _activeTab = tab;
        StateHasChanged();
    }

    // --- Child Component Handlers ---

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
        await Task.CompletedTask;
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

    // Bubble up events to parent
    private async Task HandleRequeue(string jobId) => await OnRequeue.InvokeAsync(jobId);

    private async Task HandleDelete(string jobId)
    {
        await OnDelete.InvokeAsync(jobId);
        // After deletion, close the inspector for that job
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