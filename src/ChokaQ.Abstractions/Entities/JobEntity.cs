using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// PILLAR 1: HOT DATA. Represents a record in the [Jobs] table.
/// This table is optimized for high-frequency writes and worker fetching.
/// </summary>
public record JobEntity(
    string Id,                  // Unique primary identifier (varchar 50)
    string Queue,               // Logical queue namespace (e.g., 'emails', 'billing')
    string Type,                // Job handler type key
    string? Payload,            // Raw JSON data containing job parameters
    string? Tags,               // Searchable business metadata
    string? IdempotencyKey,     // Unique token to prevent duplicate job insertion
    int Priority,               // Execution priority (Lower value = Higher priority)
    JobStatus Status,           // Lifecycle state: 0=Pending, 1=Fetched, 2=Processing
    int AttemptCount,           // Tracks number of execution retries
    string? WorkerId,           // ID of the worker instance currently processing this job
    DateTime? HeartbeatUtc,     // Last 'Keep-Alive' signal from the processing worker
    DateTime? ScheduledAtUtc,   // Delay execution until this timestamp (UTC)
    DateTime CreatedAtUtc,      // Precise moment the job was enqueued
    DateTime LastUpdatedUtc,    // Timestamp used for optimistic concurrency
    string? CreatedBy,          // Identity of the producer who created the job
    string? LastModifiedBy      // [Point 0.1] Identity of the admin who last edited or resurrected the job
);