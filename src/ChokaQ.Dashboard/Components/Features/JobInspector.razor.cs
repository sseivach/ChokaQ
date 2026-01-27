using ChokaQ.Dashboard.Models;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.Dashboard.Components.Features;

public partial class JobInspector
{
    /// <summary>
    /// Controls visibility (Two-way binding).
    /// </summary>
    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    /// <summary>
    /// The Job Data passed directly from the Feed.
    /// Eliminates the need to fetch from DB (and guess which table).
    /// </summary>
    [Parameter]
    public JobViewModel? Job { get; set; }

    // --- Actions ---

    [Parameter]
    public EventCallback<string> OnRestart { get; set; }

    [Parameter]
    public EventCallback<string> OnDelete { get; set; }

    // --- Handlers ---

    private async Task Close()
    {
        IsVisible = false;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task Restart()
    {
        if (Job is not null)
        {
            await OnRestart.InvokeAsync(Job.Id);
            await Close();
        }
    }

    private async Task Delete()
    {
        if (Job is not null)
        {
            await OnDelete.InvokeAsync(Job.Id);
            await Close();
        }
    }
}