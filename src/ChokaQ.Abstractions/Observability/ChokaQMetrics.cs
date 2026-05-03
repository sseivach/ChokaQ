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
    private readonly MetricTagLimiter _queueTags;
    private readonly MetricTagLimiter _jobTypeTags;
    private readonly MetricTagLimiter _errorTags;
    private readonly MetricTagLimiter _failureReasonTags;

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
        : this(new ChokaQMetricsOptions())
    {
    }

    public ChokaQMetrics(ChokaQMetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOrThrow();

        _queueTags = new MetricTagLimiter(
            options.MaxQueueTagValues,
            options.MaxTagValueLength,
            options.UnknownTagValue,
            options.OverflowTagValue);
        _jobTypeTags = new MetricTagLimiter(
            options.MaxJobTypeTagValues,
            options.MaxTagValueLength,
            options.UnknownTagValue,
            options.OverflowTagValue);
        _errorTags = new MetricTagLimiter(
            options.MaxErrorTagValues,
            options.MaxTagValueLength,
            options.UnknownTagValue,
            options.OverflowTagValue);
        _failureReasonTags = new MetricTagLimiter(
            options.MaxFailureReasonTagValues,
            options.MaxTagValueLength,
            options.UnknownTagValue,
            options.OverflowTagValue);

        // The meter name is the contract users wire into OpenTelemetry exporters. The version
        // describes the instrument surface, not the application package version.
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
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType)));
    }

    public void RecordSuccess(string queue, string jobType, double durationMs)
    {
        var tags = new ReadOnlySpan<KeyValuePair<string, object?>>(new[]
        {
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType))
        });

        _completedCounter.Add(1, tags);
        _durationHistogram.Record(durationMs, tags);
    }

    public void RecordFailure(string queue, string jobType, string errorType)
    {
        _failedCounter.Add(1,
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType)),
            new KeyValuePair<string, object?>("error", _errorTags.GetValue(errorType)));
    }

    public void RecordQueueLag(string queue, string jobType, double lagMs)
    {
        _queueLagHistogram.Record(lagMs,
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType)));
    }

    public void RecordDlq(string queue, string jobType, string reason)
    {
        _dlqCounter.Add(1,
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType)),
            new KeyValuePair<string, object?>("reason", _failureReasonTags.GetValue(reason)));
    }

    public void RecordRetry(string queue, string jobType, int attempt)
    {
        _retryCounter.Add(1,
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)),
            new KeyValuePair<string, object?>("type", _jobTypeTags.GetValue(jobType)),
            new KeyValuePair<string, object?>("attempt", attempt));
    }

    public void RecordActiveWorkerDelta(string queue, int delta)
    {
        _activeWorkersCounter.Add(delta,
            new KeyValuePair<string, object?>("queue", _queueTags.GetValue(queue)));
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private sealed class MetricTagLimiter
    {
        private readonly int _maxDistinctValues;
        private readonly int _maxTagValueLength;
        private readonly string _unknownTagValue;
        private readonly string _overflowTagValue;
        private readonly HashSet<string> _seenValues = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public MetricTagLimiter(
            int maxDistinctValues,
            int maxTagValueLength,
            string unknownTagValue,
            string overflowTagValue)
        {
            _maxDistinctValues = maxDistinctValues;
            _maxTagValueLength = maxTagValueLength;
            _unknownTagValue = unknownTagValue.Trim();
            _overflowTagValue = overflowTagValue.Trim();
        }

        public string GetValue(string? value)
        {
            var normalized = Normalize(value);

            // The HashSet is intentionally process-local. Exporters aggregate across processes,
            // while ChokaQ only needs to prevent one host from minting unlimited time series.
            lock (_gate)
            {
                if (_seenValues.Contains(normalized))
                {
                    return normalized;
                }

                if (_seenValues.Count >= _maxDistinctValues)
                {
                    return _overflowTagValue;
                }

                _seenValues.Add(normalized);
                return normalized;
            }
        }

        private string Normalize(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? _unknownTagValue
                : value.Trim();

            return normalized.Length <= _maxTagValueLength
                ? normalized
                : normalized[.._maxTagValueLength];
        }
    }
}
