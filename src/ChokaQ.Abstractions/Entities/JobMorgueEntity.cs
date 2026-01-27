namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// PILLAR 3: MORGUE (DLQ). Represents a record in the [JobsMorgue] table.
/// Contains jobs that failed all retry attempts. Optimized for manual review.
/// </summary>
public record JobMorgueEntity(
    string Id,                  // Original Job ID
    string Queue,               // Originating queue
    string Type,                // Failed handler type
    string? Payload,            // Preserved payload for potential fix
    string? Tags,               // Searchable metadata for troubleshooting
    string? ErrorDetails,       // Full exception stack trace and error message
    int AttemptCount,           // Total attempts made before the morgue
    string? WorkerId,           // Last worker instance that attempted execution
    string? CreatedBy,          // Identity of the original producer
    string? LastModifiedBy,     // [Point 0.1] Admin who modified the corpse or triggered resurrection
    DateTime FailedAtUtc        // Precise timestamp of the final failure
);