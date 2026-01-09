using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Represents the data transfer object for a job stored in the persistence layer.
/// Immutable record for thread safety and performance.
/// </summary>
public record JobStorageDto(
    string Id,
    string Queue,
    string Type,
    string Payload,
    JobStatus Status,
    int AttemptCount,

    int Priority,
    DateTime? ScheduledAtUtc,
    string? Tags,
    string? IdempotencyKey,
    string? WorkerId,
    string? ErrorDetails,
    string? CreatedBy,

    // Timestamps
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    DateTime LastUpdatedUtc
);