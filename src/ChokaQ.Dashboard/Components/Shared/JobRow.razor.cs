using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Shared;

public partial class JobRow
{
    [Parameter] public required JobViewModel Job { get; set; }
    [Parameter] public bool IsSelected { get; set; }

    [Parameter] public EventCallback<string> OnCancelRequested { get; set; }
    [Parameter] public EventCallback<string> OnRestartRequested { get; set; }
    [Parameter] public EventCallback<string> OnSelected { get; set; }
    [Parameter] public EventCallback<bool> OnSelectionChanged { get; set; }

    private async Task CancelJob() => await OnCancelRequested.InvokeAsync(Job.Id);
    private async Task RestartJob() => await OnRestartRequested.InvokeAsync(Job.Id);
    private async Task HandleRowClick() => await OnSelected.InvokeAsync(Job.Id);

    private async Task HandleCheckboxChange(ChangeEventArgs e)
    {
        var isChecked = (bool)(e.Value ?? false);
        await OnSelectionChanged.InvokeAsync(isChecked);
    }

    private string FormatDuration(TimeSpan? duration)
    {
        if (!duration.HasValue || duration.Value == TimeSpan.Zero) return "-";
        if (duration.Value.TotalSeconds < 1) return $"{duration.Value.TotalMilliseconds:F0}ms";
        if (duration.Value.TotalMinutes < 1) return $"{duration.Value.Seconds}s {duration.Value.Milliseconds}ms";
        return $"{duration.Value.Minutes}m {duration.Value.Seconds}s";
    }

    // --- View Logic Helpers ---

    private string GetStatusBadgeClass() => Job.Status switch
    {
        JobStatus.Processing => "bg-warning text-dark",
        JobStatus.Succeeded => "bg-success text-white",
        JobStatus.Failed => "bg-danger text-white",
        JobStatus.Cancelled => "bg-secondary text-white",
        _ => "bg-light text-dark border" // Pending
    };

    private string GetStatusLabel() => Job.Status.ToString().ToUpper();
}