using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.Json;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.JobEditor;

/// <summary>
/// Editor component. Works for both Active and DLQ jobs via a universal Hub method.
/// </summary>
public partial class JobEditor
{
    [Parameter] public required JobEditorModel Job { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnSaved { get; set; }

    // Receive connection from parent (TheDeck.razor)
    [Parameter] public HubConnection? HubConnection { get; set; }

    private string _editPayload = string.Empty;
    private int _editPriority = 10;
    private string? _errorMessage;
    private bool _isSaving;

    protected override void OnParametersSet()
    {
        if (Job != null)
        {
            // Initialize editor fields with current values
            _editPayload = Job.Payload ?? "{}";
            _editPriority = Job.Priority;
            _errorMessage = null;
        }
    }

    private async Task SaveChangesAsync()
    {
        // Basic checks
        if (Job == null || HubConnection == null)
        {
            _errorMessage = "Connection lost or Job is null.";
            return;
        }

        // Client-side JSON validation to avoid sending garbage
        if (!TryValidateJson(_editPayload, out var validationError))
        {
            _errorMessage = validationError;
            return;
        }

        try
        {
            _isSaving = true;
            _errorMessage = null;

            // Call the universal EditJob method. 
            // The Hub will check Hot storage first, then DLQ.
            bool success = await HubConnection.InvokeAsync<bool>(
                "EditJob",
                Job.Id,
                _editPayload,
                null, // Tags (reserved for future use)
                _editPriority
            );

            if (success)
            {
                // Success! Notify parent to close editor and refresh UI
                await OnSaved.InvokeAsync(Job.Id);
            }
            else
            {
                // If false returned - job exists in neither table (processed or deleted)
                _errorMessage = "Save failed. Job might have been processed or deleted externally.";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"SignalR Error: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task CancelAsync() => await OnClose.InvokeAsync();

    // Simple JSON validation (System.Text.Json)
    private bool TryValidateJson(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Payload is empty";
            return false;
        }

        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }
}