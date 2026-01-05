using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Text.Json;

namespace ChokaQ.Core.Queues;

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

        var options = new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        };
        _queue = Channel.CreateUnbounded<IChokaQJob>(options);
    }

    public ChannelReader<IChokaQJob> Reader => _queue.Reader;

    public async Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IChokaQJob
    {
        // 1. Serialize payload
        var payload = JsonSerializer.Serialize(job);

        // 2. Save to "Safe" (Storage) -> Status is Pending by default inside CreateJobAsync
        await _storage.CreateJobAsync(
             id: job.Id,
             queue: "default",
             jobType: job.GetType().AssemblyQualifiedName!,
             payload: payload,
             ct: ct
        );

        // 3. [NEW] Notify UI immediately! (Status = Pending)
        try
        {
            await _notifier.NotifyJobUpdatedAsync(job.Id, JobStatus.Pending);
        }
        catch (Exception ex)
        {
            // Don't crash the queue if SignalR is down
            _logger.LogWarning("Failed to notify UI about new job: {Message}", ex.Message);
        }

        // 4. Push to Channel for the Worker
        await _queue.Writer.WriteAsync(job, ct);
    }
}