using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Universal Data Transfer Object representing a Job in any state (Hot, Archived, or Dead).
/// This eliminates the need for multiple DTOs for different lifecycle stages.
/// </summary>
public class JobStorageDto
{
    // --- Core Identity ---
    public string Id { get; set; } = string.Empty;
    public string Queue { get; set; } = "default";
    public string Type { get; set; } = string.Empty;
    public string? Payload { get; set; }
    public int Priority { get; set; }
    public JobStatus Status { get; set; }

    // --- Reliability & Search ---
    /// <summary>
    /// Comma-separated tags for business-level filtering (e.g., "Order-123,Urgent").
    /// Indexed in both Hot and Archive storage.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// Unique key to prevent duplicate job creation.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    // --- Lifecycle Timestamps ---
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? FetchedAtUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }

    // --- Execution State ---
    public string? WorkerId { get; set; }
    public int AttemptCount { get; set; }

    // --- Archive & Morgue Specifics (Nullable) ---

    /// <summary>
    /// The exact time the job finished successfully (Archive only).
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>
    /// Execution duration in milliseconds. 
    /// Stored physically for high-performance sorting/filtering.
    /// </summary>
    public double? DurationMs { get; set; }

    /// <summary>
    /// Full exception details or error message.
    /// Populated only when the job is in the Morgue (Dead Letter Queue).
    /// </summary>
    public string? ErrorDetails { get; set; }
}