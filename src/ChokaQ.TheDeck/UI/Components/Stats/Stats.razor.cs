using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Stats;

public partial class Stats
{
    [Parameter] public StatsSummaryEntity Counts { get; set; } = new(null, 0, 0, 0, 0, 0, 0, 0, null);
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
}