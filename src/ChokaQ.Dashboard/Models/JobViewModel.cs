using ChokaQ.Abstractions.Entities;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Dashboard.Models;

/// <summary>
/// Unified view model. Now with Payload!
/// </summary>
public class JobViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Queue { get; set; } = "default";

    public JobUIStatus Status { get; set; }
    public int Priority { get; set; }
    public int AttemptCount { get; set; }

    // NEW: Needed for Inspector
    public string? Payload { get; set; }
    public string? CreatedBy { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public double? DurationMs { get; set; }
    public string? ErrorDetails { get; set; }

    // --- UI Helpers ---
    public string StatusColor => Status switch
    {
        JobUIStatus.Pending => "secondary",
        JobUIStatus.Processing => "primary",
        JobUIStatus.Succeeded => "success",
        JobUIStatus.Retrying => "warning",
        JobUIStatus.Morgue => "danger",
        JobUIStatus.Cancelled => "dark",
        _ => "secondary"
    };

    public string StatusIcon => Status switch
    {
        JobUIStatus.Pending => "bi-hourglass",
        JobUIStatus.Processing => "bi-gear-wide-connected",
        JobUIStatus.Succeeded => "bi-check-lg",
        JobUIStatus.Retrying => "bi-arrow-repeat",
        JobUIStatus.Morgue => "bi-skull",
        JobUIStatus.Cancelled => "bi-x-circle",
        _ => "bi-question"
    };

    // --- MAPPERS (Don't forget to map Payload!) ---

    public static JobViewModel FromEntity(JobEntity e) => new()
    {
        Id = e.Id,
        Type = e.Type,
        Queue = e.Queue,
        Status = e.Status == JobStatus.Fetched ? JobUIStatus.Processing : JobUIStatus.Pending,
        Priority = e.Priority,
        AttemptCount = e.AttemptCount,
        CreatedAt = e.CreatedAtUtc,
        CreatedBy = e.CreatedBy,
        Payload = e.Payload // <-- Map it
    };

    public static JobViewModel FromSucceeded(JobSucceededEntity e) => new()
    {
        Id = e.Id,
        Type = e.Type,
        Queue = e.Queue,
        Status = JobUIStatus.Succeeded,
        FinishedAt = e.FinishedAtUtc,
        DurationMs = e.DurationMs,
        CreatedBy = e.CreatedBy,
        Payload = e.Payload // <-- Map it
    };

    public static JobViewModel FromMorgue(JobMorgueEntity e) => new()
    {
        Id = e.Id,
        Type = e.Type,
        Queue = e.Queue,
        Status = JobUIStatus.Morgue,
        Priority = 0,
        AttemptCount = e.AttemptCount,
        FinishedAt = e.FailedAtUtc,
        ErrorDetails = e.ErrorDetails,
        CreatedBy = e.CreatedBy,
        Payload = e.Payload // <-- Map it
    };
}