namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Aggregated job counts for dashboard statistics.
/// Combines data from JobsHot, JobsArchive (via StatsSummary), and JobsDLQ.
/// </summary>
public record JobCountsDto(
    int Pending,
    int Fetched,
    int Processing,
    long Succeeded,
    long Failed,
    long Retried,
    long Total
);
