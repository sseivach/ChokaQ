using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Stats;

public partial class Stats
{
    [Parameter] public StatsSummaryEntity Counts { get; set; } = new(null, 0, 0, 0, 0, 0, 0, 0, null);
    [Parameter] public SystemHealthDto? Health { get; set; }
    [Parameter] public JobStatus? SelectedStatus { get; set; }
    [Parameter] public bool IsInteractive { get; set; } = true;
    [Parameter] public EventCallback<JobStatus?> OnStatusSelected { get; set; }

    private bool IsActive(JobStatus status) => SelectedStatus == status;

    private async Task HandleClick(JobStatus? status)
    {
        if (IsInteractive)
        {
            await OnStatusSelected.InvokeAsync(status);
        }
    }

    private string FormatThroughput(double? jobsPerSecond)
    {
        if (!jobsPerSecond.HasValue)
            return "0/s";

        return jobsPerSecond.Value < 10
            ? $"{jobsPerSecond.Value:0.0}/s"
            : $"{jobsPerSecond.Value:0}/s";
    }

    private static string FormatPercent(double? value) =>
        value.HasValue ? $"{value.Value:0.0}%" : "0.0%";
}
