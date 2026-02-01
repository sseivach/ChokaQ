using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents a failed job in the JobsDLQ (Dead Letter Queue) table (Third Pillar).
/// Contains jobs that cannot proceed without manual intervention.
/// </summary>
/// <remarks>
/// Jobs end up here due to:
/// - MaxRetries exhausted (FailureReason.MaxRetriesExceeded)
/// - Manual cancellation (FailureReason.Cancelled)
/// - Zombie detection (FailureReason.Zombie)
/// 
/// Supports resurrection back to JobsHot for retry with fresh state.
/// </remarks>
/// <param name="Id">Original job identifier for correlation.</param>
/// <param name="Queue">Queue where failure occurred.</param>
/// <param name="Type">Job handler type for debugging.</param>
/// <param name="Payload">Original payload for manual inspection/editing.</param>
/// <param name="Tags">Original tags preserved for filtering.</param>
/// <param name="FailureReason">Categorized reason for DLQ placement.</param>
/// <param name="ErrorDetails">Exception message, stack trace, or cancellation reason.</param>
/// <param name="AttemptCount">Number of attempts before failure.</param>
/// <param name="WorkerId">Last worker that attempted this job.</param>
/// <param name="CreatedBy">Original creator for accountability.</param>
/// <param name="LastModifiedBy">Last modifier before failure.</param>
/// <param name="CreatedAtUtc">Original job creation time.</param>
/// <param name="FailedAtUtc">When the job was moved to DLQ.</param>
public record JobDLQEntity(
    string Id,
    string Queue,
    string Type,
    string? Payload,
    string? Tags,

    FailureReason FailureReason,
    string? ErrorDetails,
    int AttemptCount,

    string? WorkerId,
    string? CreatedBy,
    string? LastModifiedBy,

    DateTime CreatedAtUtc,
    DateTime FailedAtUtc
);
