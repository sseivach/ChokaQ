namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// PILLAR 2: ARCHIVE. Represents a record in the [JobsSucceeded] table.
/// Preserved for audit logs, business intelligence, and historical analysis.
/// </summary>
public record JobSucceededEntity(
    string Id,                  // Original Job ID (Persistence)
    string Queue,               // Originating queue
    string Type,                // Executed handler type
    string? Payload,            // Full payload snapshot at completion
    string? Tags,               // Business metadata for historical search
    string? WorkerId,           // Worker instance that finished the job
    string? CreatedBy,          // Identity of the original producer
    string? LastModifiedBy,     // [Point 0.1] Last admin interaction before success
    DateTime FinishedAtUtc,     // Precise timestamp of completion
    double? DurationMs          // Total execution time in ms
);