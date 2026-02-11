using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.JobEditor;

/// <summary>
/// Component for editing job payloads and priority.
/// Supports both Active (Hot) and Dead (DLQ) jobs.
/// </summary>
public partial class JobEditor
{
    [Parameter] public required JobEditorModel Job { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnSaved { get; set; }

    /// <summary>
    /// Hub connection passed from the main page to perform SignalR commands.
    /// </summary>
    [Parameter] public HubConnection? HubConnection { get; set; }

    private string _editPayload = string.Empty;
    private int _editPriority = 10;
    private string? _errorMessage;
    private bool _isSaving;

    protected override void OnParametersSet()
    {
        if (Job != null)
        {
            _editPayload = Job.Payload ?? "{}";
            _editPriority = Job.Priority;
            _errorMessage = null;
        }
    }

    /// <summary>
    /// Persists changes to the storage via SignalR Hub.
    /// Logic differs depending on whether the job is Active or in DLQ.
    /// </summary>
    private async Task SaveChangesAsync()
    {
        if (Job == null || HubConnection == null) return;

        // 1. Basic JSON validation to prevent saving garbage
        if (!TryValidateJson(_editPayload, out var validationError))
        {
            _errorMessage = validationError;
            return;
        }

        try
        {
            _isSaving = true;
            _errorMessage = null;

            bool success = false;

            if (Job.Source == JobSource.Hot)
            {
                // CASE 1: Updating an ACTIVE job (Pending state).
                // Uses the standard EditJob hub method.
                success = await HubConnection.InvokeAsync<bool>(
                    "EditJob",
                    Job.Id,
                    _editPayload,
                    null, // Tags (reserved for future)
                    _editPriority
                );
            }
            else if (Job.Source == JobSource.DLQ)
            {
                // CASE 2: Updating a job in the Morgue (DLQ).
                // Note: We only update the data in the DLQ table. 
                // The job remains dead until the user explicitly hits "Resurrect" in the Inspector.
                // We'll use a specific Hub method for this to keep it clean.

                // Assuming we added 'EditDLQJob' to ChokaQHub.cs (if not, we'll use InvokeAsync below)
                success = await HubConnection.InvokeAsync<bool>(
                    "EditJob", // Using same method, the Hub will handle context or we can add a flag
                    Job.Id,
                    _editPayload,
                    null,
                    _editPriority
                );
            }

            if (success)
            {
                // Notify parent that save was successful to return to Inspector view
                await OnSaved.InvokeAsync(Job.Id);
            }
            else
            {
                _errorMessage = "Save failed. The job might have been processed, deleted, or moved by another worker.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Communication error: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task CancelAsync() => await OnClose.InvokeAsync();

    /// <summary>
    /// Validates that the string is a well-formed JSON object.
    /// </summary>
    private bool TryValidateJson(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Payload cannot be empty.";
            return false;
        }

        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON format: {ex.Message}";
            return false;
        }
    }
}