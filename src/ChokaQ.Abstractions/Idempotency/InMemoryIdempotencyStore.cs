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
internal sealed class InMemoryIdempotencyStore : IIdempotencyStore, IIdempotencyClaimStore
{
    private enum EntryState
    {
        InProgress,
        Completed
    }

    private sealed record CacheEntry(
        EntryState State,
        string? OwnerJobId,
        string? Payload,
        DateTimeOffset? ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly object _gate = new();

    public ValueTask<string?> TryGetResultAsync(string idempotencyKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(idempotencyKey, out var entry))
            {
                if (entry.State == EntryState.Completed && !IsExpired(entry))
                    return ValueTask.FromResult(entry.Payload);

                if (IsExpired(entry))
                    _cache.TryRemove(idempotencyKey, out _);
            }

            return ValueTask.FromResult<string?>(null);
        }
    }

    public ValueTask StoreResultAsync(string idempotencyKey, string resultPayload, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var expiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : (DateTimeOffset?)null;
        lock (_gate)
        {
            _cache[idempotencyKey] = new CacheEntry(EntryState.Completed, null, resultPayload, expiresAt);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string idempotencyKey,
        string jobId,
        TimeSpan inProgressTtl,
        CancellationToken ct = default)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(inProgressTtl);

        lock (_gate)
        {
            if (_cache.TryGetValue(idempotencyKey, out var entry))
            {
                if (entry.State == EntryState.Completed && !IsExpired(entry))
                {
                    return ValueTask.FromResult(
                        IdempotencyBeginResult.AlreadyCompleted(entry.Payload ?? ""));
                }

                if (entry.State == EntryState.InProgress && !IsExpired(entry))
                {
                    return ValueTask.FromResult(IdempotencyBeginResult.AlreadyInProgress);
                }
            }

            _cache[idempotencyKey] = new CacheEntry(EntryState.InProgress, jobId, null, expiresAt);
            return ValueTask.FromResult(IdempotencyBeginResult.Claimed);
        }
    }

    public ValueTask<bool> CompleteAsync(
        string idempotencyKey,
        string jobId,
        string completionPayload,
        TimeSpan? completedTtl = null,
        CancellationToken ct = default)
    {
        var expiresAt = completedTtl.HasValue
            ? DateTimeOffset.UtcNow.Add(completedTtl.Value)
            : (DateTimeOffset?)null;

        lock (_gate)
        {
            if (!_cache.TryGetValue(idempotencyKey, out var entry) ||
                entry.State != EntryState.InProgress ||
                !string.Equals(entry.OwnerJobId, jobId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(false);
            }

            _cache[idempotencyKey] = new CacheEntry(EntryState.Completed, null, completionPayload, expiresAt);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> ReleaseAsync(
        string idempotencyKey,
        string jobId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(idempotencyKey, out var entry) ||
                entry.State != EntryState.InProgress ||
                !string.Equals(entry.OwnerJobId, jobId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(false);
            }

            _cache.TryRemove(idempotencyKey, out _);
            return ValueTask.FromResult(true);
        }
    }

    private static bool IsExpired(CacheEntry entry) =>
        entry.ExpiresAt.HasValue && DateTimeOffset.UtcNow >= entry.ExpiresAt.Value;
}
