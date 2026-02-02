using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using Microsoft.AspNetCore.Components;
using System.Text.Json;

namespace ChokaQ.TheDeck.UI.Components.OpsPanel.JobInspector;

public partial class JobInspector
{
    [Parameter] public string? JobId { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<string> OnRestart { get; set; }
    [Parameter] public EventCallback<string> OnDelete { get; set; }

    [Inject] private IJobStorage JobStorage { get; set; } = default!;

    private JobInspectorModel? _job;
    private bool CanRequeue => _job?.Source == JobSource.DLQ;

    protected override async Task OnParametersSetAsync()
    {
        if (!string.IsNullOrEmpty(JobId))
        {
            if (_job?.Id != JobId)
            {
                _job = await FindJobAsync(JobId);
            }
        }
    }

    private async Task Close() => await OnClose.InvokeAsync();

    private async Task HandleRequeue()
    {
        if (_job != null) await OnRestart.InvokeAsync(_job.Id);
    }

    private async Task HandleDelete()
    {
        if (_job != null) await OnDelete.InvokeAsync(_job.Id);
    }

    private async Task<JobInspectorModel?> FindJobAsync(string jobId)
    {
        var hotJob = await JobStorage.GetJobAsync(jobId);
        if (hotJob != null) return new JobInspectorModel { Id = hotJob.Id, Queue = hotJob.Queue, Type = hotJob.Type, Payload = hotJob.Payload, Status = hotJob.Status, AttemptCount = hotJob.AttemptCount, Priority = hotJob.Priority, CreatedBy = hotJob.CreatedBy, CreatedAtUtc = hotJob.CreatedAtUtc, StartedAtUtc = hotJob.StartedAtUtc, Source = JobSource.Hot };

        var archiveJob = await JobStorage.GetArchiveJobAsync(jobId);
        if (archiveJob != null) return new JobInspectorModel { Id = archiveJob.Id, Queue = archiveJob.Queue, Type = archiveJob.Type, Payload = archiveJob.Payload, Status = JobStatus.Succeeded, AttemptCount = archiveJob.AttemptCount, CreatedBy = archiveJob.CreatedBy, CreatedAtUtc = archiveJob.CreatedAtUtc, StartedAtUtc = archiveJob.StartedAtUtc, FinishedAtUtc = archiveJob.FinishedAtUtc, Source = JobSource.Archive };

        var dlqJob = await JobStorage.GetDLQJobAsync(jobId);
        if (dlqJob != null) return new JobInspectorModel { Id = dlqJob.Id, Queue = dlqJob.Queue, Type = dlqJob.Type, Payload = dlqJob.Payload, Status = JobStatus.Failed, AttemptCount = dlqJob.AttemptCount, CreatedBy = dlqJob.CreatedBy, CreatedAtUtc = dlqJob.CreatedAtUtc, ErrorDetails = dlqJob.ErrorDetails, Source = JobSource.DLQ };

        return null;
    }

    private string GetStatusModifier() => _job?.Status switch
    {
        JobStatus.Failed => "inspector__banner--failed",
        JobStatus.Succeeded => "inspector__banner--succeeded",
        JobStatus.Processing => "inspector__banner--processing",
        JobStatus.Cancelled => "inspector__banner--cancelled",
        _ => "inspector__banner--info"
    };

    private string GetSourceBadge() => _job?.Source switch
    {
        JobSource.Hot => "HOT",
        JobSource.Archive => "ARCHIVE",
        JobSource.DLQ => "DLQ",
        _ => "?"
    };

    private string PrettyPrintJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try { var doc = JsonDocument.Parse(json); return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }); }
        catch { return json; }
    }

    private class JobInspectorModel
    {
        public string Id { get; init; } = "";
        public string Queue { get; init; } = "";
        public string Type { get; init; } = "";
        public string? Payload { get; init; }
        public JobStatus Status { get; init; }
        public int AttemptCount { get; init; }
        public int Priority { get; init; }
        public string? CreatedBy { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? StartedAtUtc { get; init; }
        public DateTime? FinishedAtUtc { get; init; }
        public string? ErrorDetails { get; init; }
        public JobSource Source { get; init; }
    }

    private enum JobSource { Hot, Archive, DLQ }
}
