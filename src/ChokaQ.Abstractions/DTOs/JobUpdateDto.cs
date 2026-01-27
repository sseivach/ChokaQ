using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Real-time job update payload for SignalR.
/// Designed to avoid SignalR argument limits (max 8 parameters per method).
/// </summary>
public record JobUpdateDto(
    string JobId,
    string Type,
    string Queue,
    JobStatus Status,
    int AttemptCount,
    int Priority,
    double? DurationMs,
    string? CreatedBy,
    DateTime? StartedAtUtc
);
