using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Real-time job update payload for SignalR notifications.
/// Bundled into a single DTO to avoid SignalR's 8-parameter method limit.
/// </summary>
/// <remarks>
/// Sent via IChokaQNotifier.NotifyJobUpdatedAsync when job state changes.
/// Dashboard components subscribe to "JobUpdated" hub method.
/// </remarks>
/// <param name="JobId">Unique job identifier for client-side lookup.</param>
/// <param name="Type">Job type for display and filtering.</param>
/// <param name="Queue">Queue name for categorization.</param>
/// <param name="Status">New job status after the update.</param>
/// <param name="AttemptCount">Current retry attempt number.</param>
/// <param name="Priority">Current priority (may change via dashboard).</param>
/// <param name="DurationMs">Elapsed time if processing, total time if completed.</param>
/// <param name="CreatedBy">Original creator for audit display.</param>
/// <param name="StartedAtUtc">Processing start time for duration calculation.</param>
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
