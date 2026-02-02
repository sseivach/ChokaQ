using ChokaQ.Abstractions.Workers;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Settings;

public partial class Settings
{
    [Inject] private IWorkerManager WorkerManager { get; set; } = default!;
    [Parameter] public EventCallback OnSettingsApplied { get; set; }

    public int DesiredWorkers { get; set; }
    public int MaxRetries { get; set; }
    public int RetryDelaySeconds { get; set; }

    protected override void OnInitialized()
    {
        DesiredWorkers = WorkerManager.TotalWorkers;
        MaxRetries = WorkerManager.MaxRetries;
        RetryDelaySeconds = WorkerManager.RetryDelaySeconds;
    }

    private async Task ApplyChanges()
    {
        // Validation logic
        if (RetryDelaySeconds < 1) RetryDelaySeconds = 1;
        if (DesiredWorkers < 0) DesiredWorkers = 0;
        if (DesiredWorkers > 100) DesiredWorkers = 100;

        // Apply to Singleton Manager
        WorkerManager.UpdateWorkerCount(DesiredWorkers);
        WorkerManager.MaxRetries = MaxRetries;
        WorkerManager.RetryDelaySeconds = RetryDelaySeconds;

        await OnSettingsApplied.InvokeAsync();
    }
}
