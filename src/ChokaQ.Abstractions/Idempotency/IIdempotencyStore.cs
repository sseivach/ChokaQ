namespace ChokaQ.Abstractions.Idempotency;

/// <summary>
/// Legacy completion-marker idempotency store.
/// 
/// [ARCHITECTURE PATTERN - "Idempotency as a Plugin"]:
/// Exactly-Once delivery is a distributed systems myth — you cannot guarantee it without
/// distributed transactions (2PC), which kill throughput.
///
/// The pragmatic solution is AT-LEAST-ONCE delivery + idempotent handlers:
///   - Level 1 (built-in): Deduplication via SQL UNIQUE INDEX on IdempotencyKey.
///     Prevents the same job from being ENQUEUED twice. This is free and always on.
///   - Level 2: Claim-based idempotency through IIdempotencyClaimStore.
///     This legacy interface stores completed markers and remains for source
///     compatibility with older custom stores.
///
/// New production stores should implement IIdempotencyClaimStore so concurrent
/// duplicates can be blocked before handler execution starts.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Checks whether a job with this idempotency key has a completion marker.
    /// Returns the marker payload if present, null otherwise.
    /// </summary>
    /// <param name="idempotencyKey">The unique key for this operation (e.g., "payment:{orderId}").</param>
    ValueTask<string?> TryGetResultAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Stores a completion marker for a successful execution.
    /// Subsequent calls with the same key can observe this marker.
    /// </summary>
    /// <param name="idempotencyKey">The unique key for this operation.</param>
    /// <param name="resultPayload">The serialized completion marker.</param>
    /// <param name="ttl">How long to keep this marker. Null = store indefinitely.</param>
    ValueTask StoreResultAsync(string idempotencyKey, string resultPayload, TimeSpan? ttl = null, CancellationToken ct = default);
}
