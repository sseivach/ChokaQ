using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.Components;
using System.Text.Json;

namespace ChokaQ.Dashboard.Components.Features;

/// <summary>
/// Job detail inspector supporting Three Pillars architecture.
/// Searches across Hot, Archive, and DLQ tables.
/// </summary>
public partial class JobInspector
{
    [Inject] public IJobStorage JobStorage { get; set; } = default!;

    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public string? JobId { get; set; }
    [Parameter] public EventCallback<bool> IsVisibleChanged { get; set; }
    [Parameter] public EventCallback<string> OnRestart { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    // Unified view model for any job source
    private JobInspectorModel? _job;

    private bool CanRequeue => _job?.Source == JobSource.DLQ;
    private bool CanEdit => _job?.Source == JobSource.Hot && _job?.Status == JobStatus.Pending;

    protected override async Task OnParametersSetAsync()
    {
        if (IsVisible && !string.IsNullOrEmpty(JobId))
        {
            if (_job?.Id != JobId)
            {
                _job = await FindJobAsync(JobId);
                StateHasChanged();
            }
        }
    }

    private async Task<JobInspectorModel?> FindJobAsync(string jobId)
    {
        // Search in Hot table first (most common)
        var hotJob = await JobStorage.GetJobAsync(jobId);
        if (hotJob != null)
        {
            return new JobInspectorModel
            {
                Id = hotJob.Id,
                Queue = hotJob.Queue,
                Type = hotJob.Type,
                Payload = hotJob.Payload,
                Tags = hotJob.Tags,
                Status = hotJob.Status,
                AttemptCount = hotJob.AttemptCount,
                Priority = hotJob.Priority,
                CreatedBy = hotJob.CreatedBy,
                CreatedAtUtc = hotJob.CreatedAtUtc,
                StartedAtUtc = hotJob.StartedAtUtc,
                Source = JobSource.Hot
            };
        }

        // Search in Archive
        var archiveJob = await JobStorage.GetArchiveJobAsync(jobId);
        if (archiveJob != null)
        {
            return new JobInspectorModel
            {
                Id = archiveJob.Id,
                Queue = archiveJob.Queue,
                Type = archiveJob.Type,
                Payload = archiveJob.Payload,
                Tags = archiveJob.Tags,
                Status = JobStatus.Succeeded,
                AttemptCount = archiveJob.AttemptCount,
                CreatedBy = archiveJob.CreatedBy,
                CreatedAtUtc = archiveJob.CreatedAtUtc,
                StartedAtUtc = archiveJob.StartedAtUtc,
                FinishedAtUtc = archiveJob.FinishedAtUtc,
                DurationMs = archiveJob.DurationMs,
                Source = JobSource.Archive
            };
        }

        // Search in DLQ
        var dlqJob = await JobStorage.GetDLQJobAsync(jobId);
        if (dlqJob != null)
        {
            return new JobInspectorModel
            {
                Id = dlqJob.Id,
                Queue = dlqJob.Queue,
                Type = dlqJob.Type,
                Payload = dlqJob.Payload,
                Tags = dlqJob.Tags,
                Status = JobStatus.Failed,
                AttemptCount = dlqJob.AttemptCount,
                CreatedBy = dlqJob.CreatedBy,
                CreatedAtUtc = dlqJob.CreatedAtUtc,
                FailedAtUtc = dlqJob.FailedAtUtc,
                ErrorDetails = dlqJob.ErrorDetails,
                FailureReason = dlqJob.FailureReason,
                Source = JobSource.DLQ
            };
        }

        return null;
    }

    private async Task Close()
    {
        IsVisible = false;
        _job = null;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private string GetStatusColor()
    {
        return _job?.Status switch
        {
            JobStatus.Failed => "var(--cq-danger)",
            JobStatus.Succeeded => "var(--cq-success)",
            JobStatus.Processing => "var(--cq-warning)",
            JobStatus.Cancelled => "var(--cq-text-muted)",
            _ => "var(--cq-info)"
        };
    }

    private string GetSourceBadge()
    {
        return _job?.Source switch
        {
            JobSource.Hot => "HOT",
            JobSource.Archive => "ARCHIVE",
            JobSource.DLQ => "DLQ",
            _ => "?"
        };
    }

    private string PrettyPrintJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    // Internal view model
    private class JobInspectorModel
    {
        public string Id { get; init; } = "";
        public string Queue { get; init; } = "";
        public string Type { get; init; } = "";
        public string? Payload { get; init; }
        public string? Tags { get; init; }
        public JobStatus Status { get; init; }
        public int AttemptCount { get; init; }
        public int Priority { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? FinishedAtUtc { get; init; }
        public DateTime? FailedAtUtc { get; init; }
        public double? DurationMs { get; init; }
        public string? ErrorDetails { get; init; }
        public FailureReason? FailureReason { get; init; }
        public JobSource Source { get; init; }
    }

    private enum JobSource { Hot, Archive, DLQ }
}
