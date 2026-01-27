namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents pre-aggregated statistics for a queue in the StatsSummary table.
/// Provides O(1) dashboard metrics without scanning job tables.
/// </summary>
public record StatsSummaryEntity(
    string Queue,
    long SucceededTotal,
    long FailedTotal,
    long RetriedTotal,
    DateTime? LastActivityUtc
);
