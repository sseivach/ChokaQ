using ChokaQ.Abstractions.Enums;
using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Shared;

public partial class StatsCard
{
    [Parameter] public ICollection<JobViewModel> Jobs { get; set; } = new List<JobViewModel>();
    [Parameter] public JobStatus? SelectedStatus { get; set; }
    [Parameter] public EventCallback<JobStatus?> OnStatusSelected { get; set; }

    private bool IsActive(JobStatus status) => SelectedStatus == status;

    private async Task Select(JobStatus status)
    {
        await OnStatusSelected.InvokeAsync(status);
    }

    private int GetCount(JobStatus status) => Jobs.Count(x => x.Status == status);
}