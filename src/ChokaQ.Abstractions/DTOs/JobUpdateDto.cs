using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Carries real-time update data for a job to avoid SignalR argument limits (max 8).
/// </summary>
public record JobUpdateDto(
    string JobId,
    string Type,
    JobStatus Status,
    int AttemptCount,
    double? ExecutionDurationMs,
    string? CreatedBy,
    DateTime? StartedAtUtc,
    string Queue,
    int Priority
);