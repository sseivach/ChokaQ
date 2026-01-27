namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Represents a partial update payload for a job.
/// Used when an admin edits job data via Dashboard or API (Divine Mode).
/// </summary>
public record JobDataUpdateDto(
    string JobId,               // Target Job ID to update
    string? NewPayload,         // Null = No change. Non-null = Replace payload.
    string? NewTags,            // Null = No change. Non-null = Replace tags.
    int? NewPriority,           // Null = No change. Value = Set new priority.
    string? UpdatedBy           // [Audit] Who is making this change (required for LastModifiedBy)
);