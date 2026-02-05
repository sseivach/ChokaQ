using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Workers;
using Microsoft.AspNetCore.Components;

namespace ChokaQ.TheDeck.UI.Components.Settings;

public partial class Settings
{
    [Inject] private IWorkerManager WorkerManager { get; set; } = default!;
    [Inject] private IJobStorage Storage { get; set; } = default!;
    [Parameter] public EventCallback OnSettingsApplied { get; set; }
    [Parameter] public EventCallback<(string Message, string Level)> OnLog { get; set; }
    [Parameter] public bool IsConnected { get; set; }

    public int DesiredWorkers { get; set; }
    public int MaxRetries { get; set; }
    public int RetryDelaySeconds { get; set; }

    private int _archiveRetentionDays = 30;
    private int _dlqRetentionDays = 7;
    private bool _isPurgingArchive;
    private bool _isPurgingDLQ;

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

    private async Task PurgeArchiveAsync()
    {
        if (_archiveRetentionDays < 1)
        {
            await OnLog.InvokeAsync(("Archive retention days must be at least 1.", "Warning"));
            return;
        }

        try
        {
            _isPurgingArchive = true;
            StateHasChanged();

            var cutoffDate = DateTime.UtcNow.AddDays(-_archiveRetentionDays);
            var deletedCount = await Storage.PurgeArchiveAsync(cutoffDate);

            await OnLog.InvokeAsync(($"Purged {deletedCount:N0} archived job(s) older than {_archiveRetentionDays} days.", "Info"));
        }
        catch (Exception ex)
        {
            await OnLog.InvokeAsync(($"Error purging archive: {ex.Message}", "Error"));
        }
        finally
        {
            _isPurgingArchive = false;
        }
    }

    private async Task PurgeDLQAsync()
    {
        if (_dlqRetentionDays < 1)
        {
            await OnLog.InvokeAsync(("DLQ retention days must be at least 1.", "Warning"));
            return;
        }

        try
        {
            _isPurgingDLQ = true;
            StateHasChanged();

            var cutoffDate = DateTime.UtcNow.AddDays(-_dlqRetentionDays);
            
            // Get old DLQ jobs
            var oldDLQJobs = await Storage.GetDLQJobsAsync(
                limit: 1000,
                fromDate: null,
                toDate: cutoffDate);

            var jobIds = oldDLQJobs.Select(j => j.Id).ToArray();

            if (jobIds.Length > 0)
            {
                await Storage.PurgeDLQAsync(jobIds);
                await OnLog.InvokeAsync(($"Purged {jobIds.Length:N0} DLQ job(s) older than {_dlqRetentionDays} days.", "Info"));
            }
            else
            {
                await OnLog.InvokeAsync(("No DLQ jobs to purge.", "Info"));
            }
        }
        catch (Exception ex)
        {
            await OnLog.InvokeAsync(($"Error purging DLQ: {ex.Message}", "Error"));
        }
        finally
        {
            _isPurgingDLQ = false;
        }
    }
}
