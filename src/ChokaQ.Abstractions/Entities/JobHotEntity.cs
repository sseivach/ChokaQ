using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents an active job in the JobsHot table (First Pillar).
/// Contains jobs in Pending, Fetched, or Processing states.
/// Optimized for high-frequency read/write with row-level locking.
/// </summary>
/// <remarks>
/// Lifecycle: Enqueue → Fetch → Process → Archive/DLQ
/// Jobs are removed from Hot table upon completion (success or failure).
/// </remarks>
/// <param name="Id">Unique job identifier (GUID without hyphens).</param>
/// <param name="Queue">Logical queue name for job routing (e.g., "mailing", "reports").</param>
/// <param name="Type">Fully qualified job handler type name for dispatcher resolution.</param>
/// <param name="Payload">JSON-serialized job arguments. Nullable for parameterless jobs.</param>
/// <param name="Tags">Comma-separated tags for filtering and categorization.</param>
/// <param name="IdempotencyKey">Optional key to prevent duplicate job creation.</param>
/// <param name="Priority">Execution priority (higher = sooner). Default is 10.</param>
/// <param name="Status">Current job state: Pending(0), Fetched(1), Processing(2).</param>
/// <param name="AttemptCount">Number of execution attempts. Incremented on retry.</param>
/// <param name="WorkerId">Identifier of the worker that claimed this job.</param>
/// <param name="HeartbeatUtc">Last heartbeat timestamp for zombie detection.</param>
/// <param name="ScheduledAtUtc">Earliest execution time. Null means immediate.</param>
/// <param name="CreatedAtUtc">Job creation timestamp (UTC).</param>
/// <param name="StartedAtUtc">Processing start timestamp for duration calculation.</param>
/// <param name="LastUpdatedUtc">Last modification timestamp for optimistic concurrency.</param>
/// <param name="CreatedBy">User or system that created the job (audit trail).</param>
/// <param name="LastModifiedBy">User or system that last modified the job.</param>
public record JobHotEntity(
    string Id,
    string Queue,
    string Type,
    string? Payload,
    string? Tags,
    string? IdempotencyKey,

    int Priority,
    JobStatus Status,
    int AttemptCount,

    string? WorkerId,
    DateTime? HeartbeatUtc,

    DateTime? ScheduledAtUtc,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime LastUpdatedUtc,
    string? CreatedBy,
    string? LastModifiedBy
);
