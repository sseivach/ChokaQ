namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents job statistics combining real-time Hot counts with historical totals.
/// Used for dashboard metrics and operational monitoring.
/// </summary>
/// <remarks>
/// Data sources:
/// - Pending/Fetched/Processing: Real-time COUNT from JobsHot table
/// - SucceededTotal/FailedTotal/RetriedTotal: Pre-aggregated from StatsSummary table
/// - Total: Sum of all three pillars (Hot + Archive + DLQ)
/// 
/// This hybrid approach provides O(1) dashboard reads while maintaining accuracy.
/// </remarks>
/// <param name="Queue">Queue name for per-queue stats, or null for aggregated totals.</param>
/// <param name="Pending">Jobs waiting to be picked up by workers.</param>
/// <param name="Fetched">Jobs claimed by workers but not yet processing.</param>
/// <param name="Processing">Jobs currently being executed.</param>
/// <param name="SucceededTotal">Lifetime count of successfully completed jobs.</param>
/// <param name="FailedTotal">Lifetime count of jobs in DLQ (failed + cancelled + zombie).</param>
/// <param name="RetriedTotal">Lifetime count of retry attempts across all jobs.</param>
/// <param name="Total">Total jobs across all three pillars.</param>
/// <param name="LastActivityUtc">Most recent job activity timestamp.</param>
public record StatsSummaryEntity(
    string? Queue,
    int Pending,
    int Fetched,
    int Processing,
    long SucceededTotal,
    long FailedTotal,
    long RetriedTotal,
    long Total,
    DateTime? LastActivityUtc
);
