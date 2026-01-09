using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Represents the data transfer object for a job stored in the persistence layer.
/// Immutable record for thread safety and performance.
/// </summary>
/// <param name="Id">Unique identifier of the job.</param>
/// <param name="Queue">The queue name where the job belongs.</param>
/// <param name="Type">The full type name of the job implementation.</param>
/// <param name="Payload">Serialized job arguments (JSON).</param>
/// <param name="Status">Current processing status.</param>
/// <param name="CreatedAtUtc">Timestamp when the job was created.</param>
/// <param name="LastUpdatedUtc">Timestamp of the last state change.</param>
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

    // Timestamps
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    DateTime LastUpdatedUtc
);