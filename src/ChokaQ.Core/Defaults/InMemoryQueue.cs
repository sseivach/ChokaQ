using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
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

        // Use Bounded channel to support Backpressure and prevent OutOfMemory exceptions
        var options = new BoundedChannelOptions(MaxChannelCapacity)
        {
            // If the channel is full, the Writer will wait (await) instead of throwing or dropping jobs
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _queue = Channel.CreateBounded<IChokaQJob>(options);
    }

    public ChannelReader<IChokaQJob> Reader => _queue.Reader;

    public async Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default",
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default) where TJob : IChokaQJob
    {
        // --- FAIL FAST VALIDATION ---

        if (string.IsNullOrWhiteSpace(queue))
            throw new ArgumentException("Queue name cannot be null or empty.", nameof(queue));

        if (queue.Length > 255)
            throw new ArgumentException($"Queue name '{queue}' exceeds maximum length of 255 characters.", nameof(queue));

        if (createdBy != null && createdBy.Length > 100)
            throw new ArgumentException($"CreatedBy '{createdBy}' exceeds maximum length of 100 characters.", nameof(createdBy));

        if (tags != null && tags.Length > 1000)
            throw new ArgumentException("Tags exceed maximum length of 1000 characters.", nameof(tags));

        // Resolve Key from Registry first. 
        var jobTypeName = _registry.GetKeyByType(job.GetType()) ?? job.GetType().Name;

        if (jobTypeName.Length > 255)
            throw new InvalidOperationException($"Job Type Key '{jobTypeName}' exceeds maximum length of 255 characters.");

        // ----------------------------

        // 1. Serialize payload for persistence
        var payload = JsonSerializer.Serialize(job, job.GetType());

        // 2. Persist to Storage (Hot table, Status: Pending)
        await _storage.EnqueueAsync(
             id: job.Id,
             queue: queue,
             jobType: jobTypeName,
             payload: payload,
             priority: priority,
             createdBy: createdBy,
             tags: tags,
             ct: ct
        );

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
            _logger.LogWarning("Failed to notify UI about new job: {Message}", ex.Message);
        }

        _metrics.RecordEnqueue(queue, jobTypeName);

        // 4. Push to Channel
        // If the channel reaches MaxChannelCapacity, this await will block the calling code
        // until the worker frees up space. This provides natural backpressure.
        await _queue.Writer.WriteAsync(job, ct);
    }

    public async ValueTask RequeueAsync(IChokaQJob job, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(job, ct);
    }
}