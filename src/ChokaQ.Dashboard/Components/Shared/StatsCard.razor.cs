using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Shared;

/// <summary>
/// Stats card component showing job counts by status.
/// Three Pillars: Cancelled jobs are part of DLQ (Failed).
/// </summary>
public partial class StatsCard
{
    [Parameter] public StatsSummaryEntity Counts { get; set; } = new(null, 0, 0, 0, 0, 0, 0, 0, null);
    [Parameter] public JobStatus? SelectedStatus { get; set; }
    [Parameter] public EventCallback<JobStatus?> OnStatusSelected { get; set; }

    private bool IsActive(JobStatus status) => SelectedStatus == status;

    private async Task Select(JobStatus status)
    {
        await OnStatusSelected.InvokeAsync(status);
    }

    private long GetCount(JobStatus status) => status switch
    {
        JobStatus.Pending => Counts.Pending,
        JobStatus.Fetched => Counts.Fetched,
        JobStatus.Processing => Counts.Processing,
        JobStatus.Succeeded => Counts.SucceededTotal,
        JobStatus.Failed => Counts.FailedTotal, // Includes Cancelled, Zombie in DLQ
        _ => 0
    };

    private long GetRetriedCount() => Counts.RetriedTotal;
}
