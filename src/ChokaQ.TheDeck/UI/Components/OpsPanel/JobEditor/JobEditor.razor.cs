using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Storage;
using ChokaQ.TheDeck.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;
using System.Text.Json;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.JobEditor;

public partial class JobEditor
{
    [Parameter] public required JobEditorModel Job { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnSaved { get; set; }

    [Inject] private IJobStorage JobStorage { get; set; } = default!;

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

    private async Task SaveChangesAsync()
    {
        if (Job == null) return;

        // Validate JSON
        if (!TryValidateJson(_editPayload, out var validationError))
        {
            _errorMessage = validationError;
            return;
        }

        // Validate Priority
        if (_editPriority < 0 || _editPriority > 100)
        {
            _errorMessage = "Priority must be between 0 and 100.";
            return;
        }

        try
        {
            _isSaving = true;
            _errorMessage = null;
            StateHasChanged();

            var updates = new JobDataUpdateDto(_editPayload, null, _editPriority);

            bool success;

            if (Job.Source == JobSource.Hot)
            {
                // Update existing Pending job
                success = await JobStorage.UpdateJobDataAsync(Job.Id, updates, "TheDeck");

                if (!success)
                {
                    _errorMessage = "Update failed. Job may not be in Pending status.";
                    return;
                }
            }
            else if (Job.Source == JobSource.DLQ)
            {
                // Resurrect job from DLQ with updates
                success = await JobStorage.UpdateDLQJobDataAsync(Job.Id, updates, "TheDeck");

                if (!success)
                {
                    _errorMessage = "Failed to update DLQ job. It might have been deleted or moved.";
                    return;
                }
            }
            else
            {
                _errorMessage = "Cannot edit jobs from Archive.";
                return;
            }

            if (success)
            {
                await OnSaved.InvokeAsync(Job.Id);
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task CancelAsync()
    {
        await OnClose.InvokeAsync();
    }

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
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }
    }
}
