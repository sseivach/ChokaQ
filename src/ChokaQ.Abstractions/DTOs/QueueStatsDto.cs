namespace ChokaQ.Abstractions.DTOs;

/// <summary>
/// Pre-aggregated statistics for a specific queue.
/// Maps directly to the StatsSummary table for instant dashboard rendering.
/// </summary>
public class QueueStatsDto
{
    /// <summary>
    /// The name of the queue (e.g., "default", "emails").
    /// </summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>
    /// Total number of jobs successfully processed and moved to Archive.
    /// </summary>
    public long SucceededTotal { get; set; }

    /// <summary>
    /// Total number of jobs that exhausted all retries and moved to Morgue.
    /// </summary>
    public long FailedTotal { get; set; }

    /// <summary>
    /// Timestamp of the last update to these counters.
    /// </summary>
    public DateTime LastUpdatedUtc { get; set; }
}