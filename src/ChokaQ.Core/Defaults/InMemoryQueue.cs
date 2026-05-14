using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Abstractions.Serialization;
using ChokaQ.Core;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
using ChokaQ.Core.Validation;
using Microsoft.Extensions.Logging;
using ChokaQ.Abstractions.Observability;
using System.Threading.Channels;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// High-performance, in-memory implementation of the job queue using System.Threading.Channels.
/// Acts as a Producer-Consumer buffer between the API and the Worker.
/// </summary>
internal class InMemoryQueue : IChokaQQueue
{
    private const int MaxChannelCapacity = 100_000;

    private readonly Channel<IChokaQJob> _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobTypeRegistry _registry;
    private readonly IChokaQMetrics _metrics;
    private readonly IChokaQJobSerializer _serializer;
    private readonly ILogger<InMemoryQueue> _logger;
    private readonly bool _requireRegisteredJobTypes;
    private readonly int _maxPayloadBytes;

    public InMemoryQueue(
        IJobStorage storage,
        IChokaQNotifier notifier,
        JobTypeRegistry registry,
        IChokaQMetrics metrics,
        IChokaQJobSerializer serializer,
        ILogger<InMemoryQueue> logger,
        ChokaQOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _logger = logger;
        var resolvedOptions = options ?? new ChokaQOptions();
        _requireRegisteredJobTypes = resolvedOptions.TypeResolution.RequireRegisteredJobTypes;
        _maxPayloadBytes = resolvedOptions.Serialization.MaxPayloadBytes;

        // ==============================================================================================
        // SCALABILITY: BURST HANDLING & LAYERED BACKPRESSURE
        // ==============================================================================================
        // The BoundedChannel is the first layer of backpressure in the scaling path.
        //
        // NORMAL LOAD: Jobs flow through the channel freely with near-zero latency.
        //
        // BURST / SPIKE (x10 traffic):
        //   1. Channel absorbs the spike in memory (up to MaxChannelCapacity).
        //   2. When full, EnqueueAsync.WriteAsync() BLOCKS the calling thread (natural throttle).
        //   3. Queue Lag metric spikes → triggers external autoscaling (k8s HPA, KEDA).
        //   4. New worker instances come online → lag normalizes.
        //
        // SCALABILITY PATH (when SQL becomes the bottleneck):
        //   Replace this BoundedChannel with a Kafka consumer group:
        //   - Channel.CreateBounded<IChokaQJob>() → KafkaConsumerChannel
        //   - Handler contracts (IChokaQJobHandler<T>) remain UNCHANGED.
        //   - Workers scale horizontally across consumer group partitions.
        //
        // Use BoundedChannelFullMode.Wait to apply backpressure (don't drop or throw).
        var channelOptions = new BoundedChannelOptions(MaxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // Backpressure: caller blocks when full
            SingleReader = false,                   // Multiple workers can consume concurrently
            SingleWriter = false                    // Multiple enqueues can happen concurrently
        };
        _queue = Channel.CreateBounded<IChokaQJob>(channelOptions);
    }

    public ChannelReader<IChokaQJob> Reader => _queue.Reader;

    public async Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default",
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default,
        TimeSpan? delay = null,
        string? idempotencyKey = null) where TJob : IChokaQJob
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        ChokaQEnvelopeLimits.ValidateEnvelope(queue, createdBy, tags);

        var resolvedIdempotencyKey = ResolveIdempotencyKey(job, idempotencyKey);

        var jobTypeName = _registry.GetPersistedTypeKey(job.GetType(), _requireRegisteredJobTypes);
        ChokaQEnvelopeLimits.ValidateTypeKey(jobTypeName);

        var payload = _serializer.Serialize(job, job.GetType());
        ChokaQEnvelopeLimits.ValidatePayloadSize(payload, _maxPayloadBytes);

        // 2. Persist to Storage (Hot table, Status: Pending)
        var enqueuedId = await _storage.EnqueueAsync(
             id: job.Id,
             queue: queue,
             jobType: jobTypeName,
             payload: payload,
             priority: priority,
             createdBy: createdBy,
             tags: tags,
             delay: delay,
             idempotencyKey: resolvedIdempotencyKey,
             ct: ct
        );

        if (!string.Equals(enqueuedId, job.Id, StringComparison.Ordinal))
        {
            // Enqueue idempotency is a producer-side admission control, not a worker-side filter.
            // If storage returns an existing Hot job ID, this enqueue request did not create new
            // work, so we must not notify the dashboard or write a duplicate item into the channel.
            _logger.LogDebug(
                ChokaQLogEvents.EnqueueDuplicateSkipped,
                "Skipped duplicate enqueue for idempotency key {IdempotencyKey}. Existing job: {JobId}",
                resolvedIdempotencyKey,
                enqueuedId);
            return;
        }

        // 3. Real-time Notification
        try
        {
            var update = new JobUpdateDto(
                JobId: job.Id,
                Type: jobTypeName,
                Queue: queue,
                Status: JobStatus.Pending,
                AttemptCount: 0,
                Priority: priority,
                DurationMs: null,
                CreatedBy: createdBy,
                StartedAtUtc: null
            );
            await _notifier.NotifyJobUpdatedAsync(update);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ChokaQLogEvents.EnqueueNotificationFailed,
                "Failed to notify UI about new job: {Message}",
                ex.Message);
        }

        _metrics.RecordEnqueue(queue, jobTypeName);

        // 4. Push to Channel
        // If the channel reaches MaxChannelCapacity, this await will block the calling code
        // until the worker frees up space. This provides natural backpressure.
        await _queue.Writer.WriteAsync(job, ct);
    }

    private static string? ResolveIdempotencyKey<TJob>(TJob job, string? explicitKey)
        where TJob : IChokaQJob
    {
        var key = !string.IsNullOrWhiteSpace(explicitKey)
            ? explicitKey
            : (job as IIdempotentJob)?.IdempotencyKey;

        return ChokaQEnvelopeLimits.NormalizeIdempotencyKey(key, nameof(explicitKey));
    }

    public async ValueTask RequeueAsync(IChokaQJob job, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(job, ct);
    }
}
