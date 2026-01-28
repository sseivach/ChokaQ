using ChokaQ.Abstractions;
using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Notifications;
using ChokaQ.Abstractions.Storage;
using ChokaQ.Core.Execution;
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
    private readonly JobTypeRegistry _registry;
    private readonly ILogger<InMemoryQueue> _logger;

    public InMemoryQueue(
        IJobStorage storage,
        IChokaQNotifier notifier,
        JobTypeRegistry registry,
        ILogger<InMemoryQueue> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;

        var options = new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        };
        _queue = Channel.CreateUnbounded<IChokaQJob>(options);
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
        // 1. Serialize payload for persistence
        var payload = JsonSerializer.Serialize(job, job.GetType());

        // Resolve Key from Registry first. 
        // If the user mapped this DTO in a Profile, use that Key.
        // Otherwise, fall back to the Type Name.
        var jobTypeName = _registry.GetKeyByType(job.GetType()) ?? job.GetType().Name;

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

        // 4. Push to Channel
        await _queue.Writer.WriteAsync(job, ct);
    }

    public async ValueTask RequeueAsync(IChokaQJob job, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(job, ct);
    }
}
