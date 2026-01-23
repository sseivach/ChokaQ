using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;
using System.Text.Json;

namespace ChokaQ.Dashboard.Components.Features;

public partial class JobInspector
{
    [Inject] public IJobStorage JobStorage { get; set; } = default!;

    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public string? JobId { get; set; }
    [Parameter] public EventCallback<bool> IsVisibleChanged { get; set; }
    [Parameter] public EventCallback<string> OnRestart { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    private JobStorageDto? _job;

    private bool CanRequeue => _job?.Status == JobStatus.Failed ||
                               _job?.Status == JobStatus.Cancelled ||
                               _job?.Status == JobStatus.Succeeded;

    protected override async Task OnParametersSetAsync()
    {
        if (IsVisible && !string.IsNullOrEmpty(JobId))
        {
            // Simple caching: only fetch if ID changed
            if (_job?.Id != JobId)
            {
                _job = await JobStorage.GetJobAsync(JobId);
                StateHasChanged();
            }
        }
    }

    private async Task Close()
    {
        IsVisible = false;
        _job = null;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private string GetStatusColor()
    {
        return _job?.Status switch
        {
            JobStatus.Failed => "var(--cq-danger)",
            JobStatus.Succeeded => "var(--cq-success)",
            JobStatus.Processing => "var(--cq-warning)",
            JobStatus.Cancelled => "var(--cq-text-muted)",
            _ => "var(--cq-info)"
        };
    }

    private string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}