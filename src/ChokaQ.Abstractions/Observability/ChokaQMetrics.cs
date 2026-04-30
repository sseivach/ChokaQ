using ChokaQ.Abstractions.Observability;
using System.Diagnostics.Metrics;

namespace ChokaQ.Core.Observability;

/// <summary>
/// Native OpenTelemetry implementation using System.Diagnostics.Metrics.
/// Zero external dependencies.
/// </summary>
public class ChokaQMetrics : IChokaQMetrics, IDisposable
{
    public const string MeterName = "ChokaQ";
    private readonly Meter _meter;

    private readonly Counter<long> _enqueuedCounter;
    private readonly Counter<long> _completedCounter;
    private readonly Counter<long> _failedCounter;
    private readonly Histogram<double> _durationHistogram;
    
    // Observability metrics
    private readonly Histogram<double> _queueLagHistogram;
    private readonly Counter<long> _dlqCounter;
    private readonly Counter<long> _retryCounter;
    private readonly UpDownCounter<long> _activeWorkersCounter;

    public ChokaQMetrics()
    {
        // 1.0.0 is the version of our instrument
        _meter = new Meter(MeterName, "1.0.0");

        _enqueuedCounter = _meter.CreateCounter<long>(
            "chokaq.jobs.enqueued",
            description: "Total number of jobs enqueued");

        _completedCounter = _meter.CreateCounter<long>(
            "chokaq.jobs.completed",
            description: "Total number of jobs successfully completed");

        _failedCounter = _meter.CreateCounter<long>(
            "chokaq.jobs.failed",
            description: "Total number of jobs that failed during execution");

        _durationHistogram = _meter.CreateHistogram<double>(
            "chokaq.jobs.processing_duration",
            unit: "ms",
            description: "Time taken to process a job");

        // [OBSERVABILITY PATTERN - "Saturation Signal"]:
        // Queue Lag is the golden signal for scaling asynchronous workers. 
        // It answers: "How long are jobs waiting before being picked up?"
        // High lag is the primary trigger for cluster autoscaling.
        _queueLagHistogram = _meter.CreateHistogram<double>(
            "chokaq.jobs.queue_lag",
            unit: "ms",
            description: "Time spent in queue before processing (Primary Saturation Signal)");

        _dlqCounter = _meter.CreateCounter<long>(
            "chokaq.jobs.dlq",
            description: "Total number of jobs moved to Dead Letter Queue");

        _retryCounter = _meter.CreateCounter<long>(
            "chokaq.jobs.retried",
            description: "Total number of jobs scheduled for retry");

        _activeWorkersCounter = _meter.CreateUpDownCounter<long>(
            "chokaq.workers.active",
            description: "Current number of active processing workers");
    }

    public void RecordEnqueue(string queue, string jobType)
    {
        _enqueuedCounter.Add(1,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType));
    }

    public void RecordSuccess(string queue, string jobType, double durationMs)
    {
        var tags = new ReadOnlySpan<KeyValuePair<string, object?>>(new[]
        {
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType)
        });

        _completedCounter.Add(1, tags);
        _durationHistogram.Record(durationMs, tags);
    }

    public void RecordFailure(string queue, string jobType, string errorType)
    {
        _failedCounter.Add(1,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType),
            new KeyValuePair<string, object?>("error", errorType));
    }

    public void RecordQueueLag(string queue, string jobType, double lagMs)
    {
        _queueLagHistogram.Record(lagMs,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType));
    }

    public void RecordDlq(string queue, string jobType, string reason)
    {
        _dlqCounter.Add(1,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType),
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordRetry(string queue, string jobType, int attempt)
    {
        _retryCounter.Add(1,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("type", jobType),
            new KeyValuePair<string, object?>("attempt", attempt));
    }

    public void RecordActiveWorkerDelta(string queue, int delta)
    {
        _activeWorkersCounter.Add(delta,
            new KeyValuePair<string, object?>("queue", queue));
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}