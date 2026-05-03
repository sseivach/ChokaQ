using ChokaQ.Abstractions.Observability;
using ChokaQ.Core.Observability;
using System.Diagnostics.Metrics;

namespace ChokaQ.Tests.Unit.Observability;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class ChokaQMetricsTests
{
    [Fact]
    public void ChokaQMetrics_ShouldPublishStableOpenTelemetryInstrumentContract()
    {
        using var collector = MetricCollector.Start();
        using var metrics = new ChokaQMetrics();

        const string queue = "metrics-contract-queue";
        const string jobType = "metrics_contract_job";

        metrics.RecordEnqueue(queue, jobType);
        metrics.RecordSuccess(queue, jobType, 42.5);
        metrics.RecordFailure(queue, jobType, "MetricsContractException");
        metrics.RecordQueueLag(queue, jobType, 1234.5);
        metrics.RecordDlq(queue, jobType, "MetricsContractReason");
        metrics.RecordRetry(queue, jobType, 2);
        metrics.RecordActiveWorkerDelta(queue, 1);

        // Instrument names are the public OpenTelemetry contract. Dashboards, alerts, and
        // Prometheus recording rules depend on these names staying stable across releases.
        collector.Instruments.Select(instrument => instrument.Name)
            .Should().Contain(new[]
            {
                "chokaq.jobs.enqueued",
                "chokaq.jobs.completed",
                "chokaq.jobs.failed",
                "chokaq.jobs.processing_duration",
                "chokaq.jobs.queue_lag",
                "chokaq.jobs.dlq",
                "chokaq.jobs.retried",
                "chokaq.workers.active"
            });

        collector.Instruments.Where(instrument => instrument.Name == "chokaq.jobs.processing_duration")
            .Select(instrument => instrument.Unit)
            .Should().Contain("ms");
        collector.Instruments.Where(instrument => instrument.Name == "chokaq.jobs.queue_lag")
            .Select(instrument => instrument.Unit)
            .Should().Contain("ms");

        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.enqueued"
            && GetTag(measurement, "queue") == queue
            && GetTag(measurement, "type") == jobType).Should().BeTrue();
        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.failed"
            && GetTag(measurement, "error") == "MetricsContractException").Should().BeTrue();
        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.dlq"
            && GetTag(measurement, "reason") == "MetricsContractReason").Should().BeTrue();
        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.retried"
            && GetTag(measurement, "attempt") == "2").Should().BeTrue();
    }

    [Fact]
    public void ChokaQMetrics_ShouldCollapseTagValuesAfterCardinalityBudget()
    {
        using var collector = MetricCollector.Start();
        using var metrics = new ChokaQMetrics(new ChokaQMetricsOptions
        {
            MaxQueueTagValues = 1,
            MaxJobTypeTagValues = 1,
            MaxErrorTagValues = 1,
            MaxFailureReasonTagValues = 1,
            OverflowTagValue = "__overflow__"
        });

        metrics.RecordFailure("metrics-cardinality-queue-a", "metrics_cardinality_job_a", "MetricsFirstException");
        metrics.RecordFailure("metrics-cardinality-queue-b", "metrics_cardinality_job_b", "MetricsSecondException");
        metrics.RecordDlq("metrics-cardinality-queue-a", "metrics_cardinality_job_a", "MetricsFirstReason");
        metrics.RecordDlq("metrics-cardinality-queue-b", "metrics_cardinality_job_b", "MetricsSecondReason");

        // The first distinct value is preserved for operator usefulness. Once the budget is
        // exhausted, every new value collapses into a stable bucket instead of creating a new
        // time series. This protects monitoring storage while keeping the metric queryable.
        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.failed"
            && GetTag(measurement, "queue") == "__overflow__"
            && GetTag(measurement, "type") == "__overflow__"
            && GetTag(measurement, "error") == "__overflow__").Should().BeTrue();
        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.dlq"
            && GetTag(measurement, "reason") == "__overflow__").Should().BeTrue();
    }

    [Fact]
    public void ChokaQMetrics_ShouldNormalizeBlankAndOversizedTagValues()
    {
        using var collector = MetricCollector.Start();
        using var metrics = new ChokaQMetrics(new ChokaQMetricsOptions
        {
            MaxTagValueLength = 8,
            UnknownTagValue = "unknown",
            OverflowTagValue = "other"
        });

        metrics.RecordEnqueue("   ", "very-long-dynamic-job-type-name");

        collector.Measurements.Any(measurement =>
            measurement.InstrumentName == "chokaq.jobs.enqueued"
            && GetTag(measurement, "queue") == "unknown"
            && GetTag(measurement, "type") == "very-lon").Should().BeTrue();
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _gate = new();
        private readonly List<Instrument> _instruments = new();
        private readonly List<CapturedMeasurement> _measurements = new();

        private MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != ChokaQMetrics.MeterName)
                    return;

                lock (_gate)
                {
                    _instruments.Add(instrument);
                }

                listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
                Capture(instrument, measurement, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
                Capture(instrument, measurement, tags));
        }

        public IReadOnlyList<Instrument> Instruments
        {
            get
            {
                lock (_gate)
                {
                    return _instruments.ToArray();
                }
            }
        }

        public IReadOnlyList<CapturedMeasurement> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return _measurements.ToArray();
                }
            }
        }

        public static MetricCollector Start()
        {
            var collector = new MetricCollector();
            collector._listener.Start();
            return collector;
        }

        public void Dispose() => _listener.Dispose();

        private void Capture(
            Instrument instrument,
            double measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var capturedTags = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                capturedTags[tag.Key] = tag.Value;
            }

            lock (_gate)
            {
                _measurements.Add(new CapturedMeasurement(instrument.Name, measurement, capturedTags));
            }
        }
    }

    private sealed record CapturedMeasurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    private static string? GetTag(CapturedMeasurement measurement, string key) =>
        measurement.Tags.TryGetValue(key, out var value) ? value?.ToString() : null;
}
