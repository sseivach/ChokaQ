using ChokaQ.Abstractions.Enums;
using ChokaQ.TheDeck.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.JobMatrix.JobRow;

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

    private string GetStatusModifier() => Job.Status switch
    {
        JobStatus.Processing => "job-row__status--processing",
        JobStatus.Fetched => "job-row__status--fetched",
        JobStatus.Succeeded => "job-row__status--succeeded",
        JobStatus.Failed => "job-row__status--failed",
        JobStatus.Cancelled => "job-row__status--cancelled",
        _ => ""
    };

    private string GetAttemptsModifier() => Job.Attempts switch
    {
        <= 1 => "",
        2 => "job-row__badge--attempts-warning",
        _ => "job-row__badge--attempts-danger"
    };

    private string GetStatusLabel() => Job.Status.ToString().ToUpper();
}
