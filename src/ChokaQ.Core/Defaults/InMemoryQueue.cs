using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Channels;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// High-performance, in-memory implementation of the job queue using System.Threading.Channels.
/// Acts as a Producer-Consumer buffer between the API and the Worker.
/// </summary>
public class InMemoryQueue : IChokaQQueue
{
    private readonly Channel<IChokaQJob> _queue;
    private readonly IJobStorage _storage;
    private readonly IChokaQNotifier _notifier;
    private readonly ILogger<InMemoryQueue> _logger;

    public InMemoryQueue(
        IJobStorage storage,
        IChokaQNotifier notifier,
        ILogger<InMemoryQueue> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger;

        // Unbounded channel: Can accept any number of items.
        // Good for high throughput, but watch out for memory usage in production.
        var options = new UnboundedChannelOptions
        {
            SingleReader = false, // Multiple workers can read (Scalability ready)
            SingleWriter = false  // Multiple threads can write (API ready)
        };
        _queue = Channel.CreateUnbounded<IChokaQJob>(options);
    }

    /// <summary>
    /// Exposes the reader side of the channel for consumers (Workers).
    /// </summary>
    public ChannelReader<IChokaQJob> Reader => _queue.Reader;

    /// <inheritdoc />
    public async Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default) where TJob : IChokaQJob
    {
        // 1. Serialize payload for persistence
        var payload = JsonSerializer.Serialize(job);
        var jobTypeName = job.GetType().Name; // Get simple class name for UI

        // 2. Persist to Storage (Status: Pending)
        // We save BEFORE enqueueing to ensure data safety.
        await _storage.CreateJobAsync(
             id: job.Id,
             queue: "default",
             jobType: job.GetType().AssemblyQualifiedName!,
             payload: payload,
             priority: priority,   // Pass priority
             createdBy: createdBy, // Pass user
             tags: tags,           // Pass tags
             ct: ct
        );

        // 3. Real-time Notification
        // We notify the UI immediately so the user sees the job in the "Pending" state.
        try
        {
            await _notifier.NotifyJobUpdatedAsync(
                job.Id,
                jobTypeName,
                JobStatus.Pending,
                0,
                null,
                createdBy,
                null
            );
        }
        catch (Exception ex)
        {
            // Logging warning only; we don't want to fail the job just because SignalR failed.
            _logger.LogWarning("Failed to notify UI about new job: {Message}", ex.Message);
        }

        // 4. Push to Channel
        // This makes the job available for the Background Worker.
        await _queue.Writer.WriteAsync(job, ct);
    }

    /// <summary>
    /// Pushes a job back to the channel WITHOUT creating a new storage record.
    /// Used for Retries.
    /// </summary>
    public async ValueTask RequeueAsync(IChokaQJob job, CancellationToken ct = default)
    {
        // Simply write to the channel.
        // Storage already knows about this job.
        await _queue.Writer.WriteAsync(job, ct);
    }
}