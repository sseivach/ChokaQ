using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// PILLAR 1: HOT DATA. Represents a record in the [Jobs] table.
/// This table is optimized for high-frequency writes and worker fetching.
/// </summary>
public record Job(
    string Id,                  // Unique primary identifier (GUID or string)
    string Queue,               // Logical queue namespace (e.g., 'emails', 'billing')
    string Type,                // Job handler type key (mapped via Profiles)
    string? Payload,            // Raw JSON data containing job parameters

    /// <summary>
    /// Searchable business metadata. 
    /// Max index key size is 1700 bytes. Capped at 1000 to stay well under the limit.
    /// </summary>
    string? Tags,

    string? IdempotencyKey,     // Unique token to prevent duplicate job insertion
    int Priority,               // Execution priority (Lower value = Higher priority)
    JobStatus Status,           // Lifecycle state: 0=Pending, 1=Fetched, 2=Processing
    int AttemptCount,           // Tracks number of execution retries
    string? WorkerId,           // ID of the worker instance currently processing this job
    DateTime? HeartbeatUtc,     // Last 'Keep-Alive' signal from the processing worker
    DateTime? ScheduledAtUtc,   // Delay execution until this timestamp (UTC)
    DateTime CreatedAtUtc,      // Precise moment the job was enqueued
    DateTime LastUpdatedUtc,    // Timestamp used for optimistic concurrency and tracking
    string? CreatedBy           // Identity of the producer who created the job
);

/// <summary>
/// PILLAR 2: ARCHIVE. Represents a record in the [JobsSucceeded] table.
/// Preserved for audit logs, business intelligence, and historical analysis.
/// </summary>
public record JobSucceeded(
    string Id,                  // Original Job ID
    string Queue,               // Originating queue
    string Type,                // Executed handler type
    string? Payload,            // Full payload snapshot at the moment of completion
    string? Tags,               // Business metadata for historical search
    string? WorkerId,           // Worker instance that successfully finished the work
    string? CreatedBy,          // Identity of the original producer
    DateTime FinishedAtUtc,     // Precise timestamp of successful completion
    double? DurationMs          // Total execution time in milliseconds for telemetry
);

/// <summary>
/// PILLAR 3: MORGUE (DLQ). Represents a record in the [JobsMorgue] table.
/// Contains jobs that failed all retry attempts. Optimized for manual review and resurrection.
/// </summary>
public record JobMorgue(
    string Id,                  // Original Job ID
    string Queue,               // Originating queue
    string Type,                // Failed handler type
    string? Payload,            // Preserved payload for potential fix and resurrection
    string? Tags,               // Searchable metadata for troubleshooting
    string? ErrorDetails,       // Full exception stack trace and error message
    int AttemptCount,           // Total attempts made before the job was moved to morgue
    string? WorkerId,           // Last worker instance that attempted execution
    string? CreatedBy,          // Identity of the original producer
    DateTime FailedAtUtc        // Precise timestamp of the final failure
);

/// <summary>
/// AGGREGATED STATS. Represents a record in the [StatsSummary] table.
/// Provides O(1) performance for dashboard metrics by avoiding heavy COUNT(*) scans.
/// </summary>
public record StatsSummary(
    string Queue,               // Unique queue name (Primary Key)
    long SucceededTotal,        // Global counter of all successful jobs
    long FailedTotal,           // Global counter of all permanently failed jobs
    long RetriedTotal,          // Global counter of all transient failures (retries)
    DateTime? LastActivityUtc   // Last time any activity was recorded for this queue
);

/// <summary>
/// QUEUE CONFIGURATION. Represents a record in the [Queues] table.
/// Acts as the control plane for individual queue behavior.
/// </summary>
public record Queue(
    string Name,                // Unique queue name (Primary Key)
    bool IsPaused,              // Flag: 1=Workers stop fetching from this queue
    bool IsActive,              // Flag: 0=Hide queue from monitoring/dashboard
    DateTime LastUpdatedUtc,    // Audit timestamp for configuration changes
    int? ZombieTimeoutSeconds   // Custom heartbeat threshold for this specific queue
);