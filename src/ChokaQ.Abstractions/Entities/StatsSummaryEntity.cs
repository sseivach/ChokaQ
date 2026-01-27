namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// AGGREGATED STATS. Represents a record in the [StatsSummary] table.
/// Provides O(1) performance for dashboard metrics.
/// </summary>
public record StatsSummaryEntity(
    string Queue,               // Unique queue name (Primary Key)
    long SucceededTotal,        // Global counter of all successful jobs
    long FailedTotal,           // Global counter of all permanently failed jobs
    long RetriedTotal,          // Global counter of all transient failures
    DateTime? LastActivityUtc   // Last activity heartbeat
);