namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents job statistics. Can be used for:
/// - Per-queue stats (Queue = "mailing", "reports", etc.)
/// - Aggregated stats (Queue = "*" or null)
/// Maps to StatsSummary table for historical totals, combined with real-time Hot counts.
/// </summary>
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
