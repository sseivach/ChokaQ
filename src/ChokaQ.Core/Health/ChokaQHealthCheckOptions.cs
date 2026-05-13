namespace ChokaQ.Core.Health;

/// <summary>
/// Configures ChokaQ health-check thresholds.
/// </summary>
/// <remarks>
/// Health checks are an operator contract, so thresholds should be explicit and easy to tune.
/// The defaults match the dashboard saturation colors: under 5 seconds is healthy, 5-10 seconds
/// is degraded, and over 10 seconds is unhealthy.
/// </remarks>
public sealed class ChokaQHealthCheckOptions
{
    /// <summary>
    /// Conventional child section under "ChokaQ" used by configuration-binding overloads.
    /// </summary>
    public const string SectionName = "Health";

    /// <summary>
    /// Maximum age of the worker-loop heartbeat before the process is considered unhealthy.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan WorkerHeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Queue lag above this value makes the queue-saturation health check degraded.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan QueueLagDegradedThreshold { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Queue lag above this value makes the queue-saturation health check unhealthy.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan QueueLagUnhealthyThreshold { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Throws a startup exception when health-check thresholds are contradictory or unsafe.
    /// </summary>
    public void ValidateOrThrow()
    {
        if (WorkerHeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ChokaQ health check WorkerHeartbeatTimeout must be greater than zero.");
        }

        if (QueueLagDegradedThreshold < TimeSpan.Zero)
        {
            throw new InvalidOperationException("ChokaQ health check QueueLagDegradedThreshold must not be negative.");
        }

        if (QueueLagUnhealthyThreshold <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("ChokaQ health check QueueLagUnhealthyThreshold must be greater than zero.");
        }

        if (QueueLagUnhealthyThreshold < QueueLagDegradedThreshold)
        {
            throw new InvalidOperationException("ChokaQ health check QueueLagUnhealthyThreshold must be greater than or equal to QueueLagDegradedThreshold.");
        }
    }
}
