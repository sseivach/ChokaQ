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

    /// <summary>
    /// Records the current processing lag (how long jobs stay in Pending state).
    /// 
    /// [OBSERVABILITY PATTERN - "Saturation Signal"]:
    /// Queue Lag is the primary indicator for background processing health. 
    /// Best practices recommend monitoring saturation over throughput:
    /// if lag increases, it's a direct signal to scale the worker cluster.
    /// It acts as the key SLI (Service Level Indicator) for autoscaling.
    /// </summary>
    void RecordQueueLag(string queue, string jobType, double lagMs);

    /// <summary>
    /// Records that a job was moved to the Dead Letter Queue.
    /// </summary>
    void RecordDlq(string queue, string jobType, string reason);

    /// <summary>
    /// Records that a job is scheduled for a retry.
    /// </summary>
    void RecordRetry(string queue, string jobType, int attempt);

    /// <summary>
    /// Tracks the active worker count (traffic). Delta is usually +1 or -1.
    /// </summary>
    void RecordActiveWorkerDelta(string queue, int delta);
}