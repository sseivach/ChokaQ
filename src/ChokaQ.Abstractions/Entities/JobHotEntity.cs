using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents an active job in the JobsHot table.
/// Hot data optimized for high-frequency read/write operations.
/// </summary>
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
