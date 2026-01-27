using ChokaQ.Abstractions;
using Microsoft.Extensions.Logging;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Adapts the IJobStorage to the typed IChokaQQueue interface (Bus Mode).
/// </summary>
public class InMemoryQueue : IChokaQQueue
{
    private readonly IJobStorage _storage;
    private readonly ILogger<InMemoryQueue> _logger;

    public InMemoryQueue(IJobStorage storage, ILogger<InMemoryQueue> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task EnqueueAsync<TJob>(
        TJob job,
        int priority = 10,
        string queue = "default",
        string? createdBy = null,
        string? tags = null,
        CancellationToken ct = default) where TJob : IChokaQJob
    {
        string type = typeof(TJob).Name;

        var payloadJson = System.Text.Json.JsonSerializer.Serialize(job);

        await _storage.EnqueueAsync(
            queue: queue,
            jobType: type,
            payload: payloadJson,
            priority: priority,
            createdBy: createdBy ?? "System",
            tags: tags,
            delay: null,
            idempotencyKey: null,
            ct: ct
        );
    }
}