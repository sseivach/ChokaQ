using ChokaQ.Abstractions.Enums;

namespace ChokaQ.TheDeck.Models;

public class JobViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Queue { get; set; } = "default";
    public string Type { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int Priority { get; set; }

    public DateTime AddedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Progress { get; set; } = 0;
    public string? CreatedBy { get; set; }
    public DateTime? StartedAtUtc { get; set; }

    public string Payload { get; set; } = "{}";
    public string? ErrorDetails { get; set; }

    /// <summary>
    /// Normalized, short failure family shown directly in the DLQ grid.
    /// </summary>
    /// <remarks>
    /// Operators need the taxonomy badge for routing ("timeout", "fatal", "throttled"),
    /// but they also need the repeated error family to decide whether many rows are the same
    /// incident. The full stack trace stays in ErrorDetails and the inspector; this value is
    /// deliberately compact so the table remains scannable under production volume.
    /// </remarks>
    public string? ErrorFamily { get; set; }

    public FailureReason? FailureReason { get; set; }
}
