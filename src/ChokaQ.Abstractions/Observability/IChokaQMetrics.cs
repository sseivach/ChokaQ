namespace ChokaQ.Abstractions.Observability;

/// <summary>
/// Defines the contract for tracking OpenTelemetry metrics.
/// </summary>
public interface IChokaQMetrics
{
    /// <summary>
    /// Records that a new job was enqueued.
    /// </summary>
    void RecordEnqueue(string queue, string jobType);

    /// <summary>
    /// Records a successful job execution and its duration.
    /// </summary>
    void RecordSuccess(string queue, string jobType, double durationMs);

    /// <summary>
    /// Records a job failure.
    /// </summary>
    void RecordFailure(string queue, string jobType, string errorType);
}