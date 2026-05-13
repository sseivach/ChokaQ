using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
using ChokaQ.Core.Observability;
using Microsoft.Extensions.Logging;
using ChokaQ.Abstractions.Observability;
using System.Text.Json;
using System.Threading.Channels;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// High-performance, in-memory implementation of the job queue using System.Threading.Channels.
/// Acts as a Producer-Consumer buffer between the API and the Worker.
/// </summary>
public class InMemoryQueue : IChokaQQueue
{
    private const int MaxChannelCapacity = 100_000;

    private readonly Channel<IChokaQJob> _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly JobTypeRegistry _registry;
    private readonly IChokaQMetrics _metrics;
    private readonly ILogger<InMemoryQueue> _logger;

    public InMemoryQueue(
        IJobStorage storage,
        IChokaQNotifier notifier,
        JobTypeRegistry registry,
        IChokaQMetrics metrics,
        ILogger<InMemoryQueue> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger;

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
        // --- FAIL FAST VALIDATION ---

        if (job is null)
            throw new ArgumentNullException(nameof(job));

        if (string.IsNullOrWhiteSpace(queue))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(queue));

        if (queue.Length > 255)
            throw new ArgumentException($"Queue name '{queue}' exceeds maximum length of 255 characters.", nameof(queue));

        if (createdBy != null && createdBy.Length > 100)
            throw new ArgumentException($"CreatedBy '{createdBy}' exceeds maximum length of 100 characters.", nameof(createdBy));

        if (tags != null && tags.Length > 1000)
            throw new ArgumentException("Tags exceed maximum length of 1000 characters.", nameof(tags));

        var resolvedIdempotencyKey = ResolveIdempotencyKey(job, idempotencyKey);

        // Resolve Key from Registry first. 
        var jobTypeName = _registry.GetKeyByType(job.GetType()) ?? job.GetType().Name;

        if (jobTypeName.Length > 255)
            throw new InvalidOperationException($"Job Type Key '{jobTypeName}' exceeds maximum length of 255 characters.");

        // ----------------------------

        // 1. Serialize payload for persistence
        var payload = JsonSerializer.Serialize(job, job.GetType());

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

        if (string.IsNullOrWhiteSpace(key))
            return null;

        key = key.Trim();
        if (key.Length > 255)
        {
            // The storage schema uses varchar(255). Failing before serialization and persistence
            // gives callers a clear contract violation instead of a provider-specific SQL error.
            throw new ArgumentException("IdempotencyKey exceeds maximum length of 255 characters.", nameof(explicitKey));
        }

        return key;
    }

    public async ValueTask RequeueAsync(IChokaQJob job, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(job, ct);
    }
}
