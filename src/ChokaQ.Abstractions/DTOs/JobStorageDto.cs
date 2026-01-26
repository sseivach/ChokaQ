using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Universal Data Transfer Object representing a Job.
/// Maps 1:1 to SQL tables (Hot, Archive, Morgue).
/// </summary>
public class JobStorageDto
{
    public string Id { get; set; } = default!;
    public string Queue { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string? Payload { get; set; }
    public int Priority { get; set; }
    public JobStatus Status { get; set; }
    public string? Tags { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? CreatedBy { get; set; }

    // --- Timestamps ---
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }

    // Lifecycle timestamps
    public DateTime? FetchedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }

    // Audit timestamp
    public DateTime LastUpdatedUtc { get; set; }

    // --- Execution Details ---
    public string? WorkerId { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorDetails { get; set; }

    // Metrics
    public double? DurationMs { get; set; }
}