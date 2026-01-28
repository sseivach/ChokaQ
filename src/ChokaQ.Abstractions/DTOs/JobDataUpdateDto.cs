namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Unified DTO for editing job data (Payload, Tags, Priority).
/// Used for Hot Editing (Pending jobs) and Resurrection (DLQ jobs).
/// </summary>
public record JobDataUpdateDto(
    string? Payload,
    string? Tags,
    int? Priority
)
{
    /// <summary>
    /// Creates an empty update (no changes).
    /// </summary>
    public static JobDataUpdateDto Empty => new(null, null, null);

    /// <summary>
    /// Checks if any field is set for update.
    /// </summary>
    public bool HasChanges => Payload is not null || Tags is not null || Priority is not null;
}
