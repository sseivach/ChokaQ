namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents a successfully completed job in the JobsArchive table.
/// Optimized for long-term storage and audit queries.
/// </summary>
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
