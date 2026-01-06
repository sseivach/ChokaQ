using ChokaQ.Abstractions.Enums;
using ChokaQ.Core.Workers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChokaQ.Dashboard;

public partial class ChokaQDashboard : IAsyncDisposable
{
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    [Inject] public IWorkerManager WorkerManager { get; set; } = default!;

    private HubConnection? _hubConnection;
    private List<JobViewModel> _jobs = new();

    // UI State
    private int _desiredWorkers = 1;
    private int _maxRetries = 3;
    private int _retryDelaySeconds = 3;

    private bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    protected override async Task OnInitializedAsync()
    {
        // Load initial state
        _desiredWorkers = WorkerManager.ActiveWorkers;
        _maxRetries = WorkerManager.MaxRetries;
        _retryDelaySeconds = WorkerManager.RetryDelaySeconds;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(Navigation.ToAbsoluteUri("/chokaq-hub"))
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<string, int, int>("JobUpdated", (jobId, statusInt, attempts) =>
        {
            var status = (JobStatus)statusInt;
            InvokeAsync(() =>
            {
                UpdateJob(jobId, status, attempts);
                StateHasChanged();
            });
        });

        await _hubConnection.StartAsync();
    }

    private void UpdateJob(string jobId, JobStatus status, int attempts)
    {
        var existing = _jobs.FirstOrDefault(j => j.Id == jobId);
        var now = DateTime.Now;

        if (existing != null)
        {
            existing.Status = status;
            existing.Attempts = attempts;

            if (status == JobStatus.Succeeded || status == JobStatus.Failed)
            {
                existing.Duration = now - existing.AddedAt;
            }
        }
        else
        {
            _jobs.Add(new JobViewModel
            {
                Id = jobId,
                Status = status,
                Attempts = attempts,
                AddedAt = now,
                Duration = TimeSpan.Zero
            });

            if (_jobs.Count > 100) _jobs.RemoveAt(0);
        }
    }

    private void UpdateSettings()
    {
        // Validate Delay Input
        if (_retryDelaySeconds < 1) _retryDelaySeconds = 1;
        if (_retryDelaySeconds > 3600) _retryDelaySeconds = 3600;

        // Apply settings
        WorkerManager.UpdateWorkerCount(_desiredWorkers);
        WorkerManager.MaxRetries = _maxRetries;
        WorkerManager.RetryDelaySeconds = _retryDelaySeconds;

        StateHasChanged();
    }

    private void ClearHistory()
    {
        _jobs.Clear();
    }

    // --- UI Helpers ---

    private string GetBadgeClass(JobStatus status) => status switch
    {
        JobStatus.Pending => "bg-secondary text-white",
        JobStatus.Processing => "bg-warning text-dark",
        JobStatus.Succeeded => "bg-success text-white",
        JobStatus.Failed => "bg-danger text-white",
        _ => "bg-light text-dark"
    };

    private string GetStatusIcon(JobStatus status) => status switch
    {
        JobStatus.Pending => "⏳",
        JobStatus.Processing => "⚙️",
        JobStatus.Succeeded => "✅",
        JobStatus.Failed => "❌",
        _ => "❓"
    };

    private string FormatDuration(TimeSpan? duration)
    {
        if (!duration.HasValue || duration.Value == TimeSpan.Zero) return "-";

        if (duration.Value.TotalSeconds < 1)
            return $"{duration.Value.Milliseconds}ms";

        if (duration.Value.TotalMinutes < 1)
            return $"{duration.Value.Seconds}s {duration.Value.Milliseconds}ms";

        return $"{duration.Value.Minutes}m {duration.Value.Seconds}s";
    }

    private string GetRowClass(JobViewModel job)
    {
        return job.Status == JobStatus.Processing ? "table-active" : "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null) await _hubConnection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public class JobViewModel
    {
        public string Id { get; set; } = "";
        public JobStatus Status { get; set; }
        public int Attempts { get; set; }
        public DateTime AddedAt { get; set; }
        public TimeSpan? Duration { get; set; }
    }
}