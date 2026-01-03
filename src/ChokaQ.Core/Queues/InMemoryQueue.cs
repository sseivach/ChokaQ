using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Storage;
using System.Threading.Channels;
using System.Text.Json;

namespace ChokaQ.Core.Queues;

public class InMemoryQueue : IChokaQQueue
{
    private readonly Channel<IChokaQJob> _queue;
    private readonly IJobStorage _storage;

    public InMemoryQueue(IJobStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));

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

        // 2. Save to "Safe" (Storage) using the Job's ID
        await _storage.CreateJobAsync(
             id: job.Id,
             queue: "default",
             jobType: job.GetType().AssemblyQualifiedName!,
             payload: payload,
             ct: ct
        );

        // 3. Push to Channel
        await _queue.Writer.WriteAsync(job, ct);
    }
}