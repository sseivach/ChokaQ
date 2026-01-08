using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Dashboard.Models;

public class JobViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime AddedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Progress { get; set; } = 0;
}