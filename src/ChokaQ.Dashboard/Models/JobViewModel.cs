using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Dashboard.Models;

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
}