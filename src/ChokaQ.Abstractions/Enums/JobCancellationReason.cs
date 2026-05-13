namespace ChokaQ.Abstractions.Enums;

/// <summary>
/// Formalizes the reasons a job can be cancelled, enabling analytical insights into system health.
/// </summary>
public enum JobCancellationReason
{
    Shutdown,
    Timeout,
    Admin,
    HeartbeatFailure
}
