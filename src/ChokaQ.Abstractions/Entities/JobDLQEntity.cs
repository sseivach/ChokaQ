using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents a failed job in the JobsDLQ (Dead Letter Queue) table.
/// Stores jobs that exhausted all retry attempts, were cancelled, or became zombies.
/// Supports manual review, resurrection, and operational analytics.
/// </summary>
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
