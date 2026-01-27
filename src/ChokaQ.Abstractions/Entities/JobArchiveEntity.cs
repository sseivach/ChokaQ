namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents a successfully completed job in the JobsArchive table (Second Pillar).
/// Contains immutable historical records of succeeded jobs.
/// Optimized for analytical queries with page compression enabled.
/// </summary>
/// <remarks>
/// Jobs are moved here atomically from JobsHot upon successful completion.
/// Supports retention policies via scheduled purge operations.
/// </remarks>
/// <param name="Id">Original job identifier from JobsHot.</param>
/// <param name="Queue">Queue where the job was processed.</param>
/// <param name="Type">Job handler type that executed this job.</param>
/// <param name="Payload">Original job payload (preserved for audit/replay).</param>
/// <param name="Tags">Original tags for historical filtering.</param>
/// <param name="AttemptCount">Final attempt count (1 = first-try success).</param>
/// <param name="WorkerId">Worker that successfully completed this job.</param>
/// <param name="CreatedBy">Original creator for audit trail.</param>
/// <param name="LastModifiedBy">Last modifier before archival.</param>
/// <param name="CreatedAtUtc">Original job creation time.</param>
/// <param name="StartedAtUtc">When processing began.</param>
/// <param name="FinishedAtUtc">When processing completed successfully.</param>
/// <param name="DurationMs">Total execution time in milliseconds.</param>
public record JobArchiveEntity(
    string Id,
    string Queue,
    string Type,
    string? Payload,
    string? Tags,
    int AttemptCount,
    string? WorkerId,
    string? CreatedBy,
    string? LastModifiedBy,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime FinishedAtUtc,
    double? DurationMs
);
