using System.Threading.Channels;
using ChokaQ.Abstractions;

namespace ChokaQ.Core.Queues;

public class InMemoryQueue : IChokaQQueue
{
    private readonly Channel<IChokaQJob> _queue;

    public InMemoryQueue()
    {
        // Unbounded means the queue has no capacity limit (RAM-limited only).
        // TODO: check later if we want to have bounded channels with backpressure.
        var options = new UnboundedChannelOptions
        {
            SingleReader = false,   // multiple readers can read concurrently
            SingleWriter = false    // multiple writers can write concurrently
        };

        _queue = Channel.CreateUnbounded<IChokaQJob>(options);
    }

    /// <summary>
    /// Exposes the reader for the background worker to consume jobs.
    /// </summary>
    public ChannelReader<IChokaQJob> Reader => _queue.Reader;

    public async Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : IChokaQJob
    {
        // Simply writing to the channel. It's thread-safe and non-blocking.
        await _queue.Writer.WriteAsync(job, ct);
    }
}