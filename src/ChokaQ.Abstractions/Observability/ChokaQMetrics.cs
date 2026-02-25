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

    public void Dispose()
    {
        _meter.Dispose();
    }
}