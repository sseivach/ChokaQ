using System.Collections.Concurrent;

namespace ChokaQ.Abstractions.Idempotency;

/// <summary>
/// In-memory implementation of IIdempotencyStore for development and testing.
/// 
/// [IMPORTANT]: This implementation is NOT suitable for production because:
/// 1. State is lost on process restart.
/// 2. State is not shared across multiple instances (horizontal scaling).
/// 
/// For production, replace with a Redis or SQL-backed implementation:
///   services.AddResultIdempotency&lt;RedisIdempotencyStore&gt;();
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private record CacheEntry(string Payload, DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public ValueTask<string?> TryGetResultAsync(string idempotencyKey, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(idempotencyKey, out var entry))
        {
            // Respect TTL if set
            if (entry.ExpiresAt == null || DateTimeOffset.UtcNow < entry.ExpiresAt)
                return ValueTask.FromResult<string?>(entry.Payload);

            _cache.TryRemove(idempotencyKey, out _);
        }
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask StoreResultAsync(string idempotencyKey, string resultPayload, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : (DateTimeOffset?)null;
        _cache[idempotencyKey] = new CacheEntry(resultPayload, expiresAt);
        return ValueTask.CompletedTask;
    }
}
