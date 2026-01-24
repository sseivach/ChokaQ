using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Shared;

public partial class StatsCard
{
    [Parameter] public JobCountsDto Counts { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
    [Parameter] public JobStatus? SelectedStatus { get; set; }
    [Parameter] public EventCallback<JobStatus?> OnStatusSelected { get; set; }

    private bool IsActive(JobStatus status) => SelectedStatus == status;

    private async Task Select(JobStatus status)
    {
        await OnStatusSelected.InvokeAsync(status);
    }

    private int GetCount(JobStatus status) => status switch
    {
        JobStatus.Pending => Counts.Pending,
        JobStatus.Fetched => Counts.Fetched,
        JobStatus.Processing => Counts.Processing,
        JobStatus.Succeeded => Counts.Succeeded,
        JobStatus.Failed => Counts.Failed,
        JobStatus.Cancelled => Counts.Cancelled,
        _ => 0
    };
}