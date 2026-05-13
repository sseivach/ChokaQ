namespace ChokaQ.Abstractions.Idempotency;

/// <summary>
/// Defines the contract for the Result-Based Idempotency Store.
/// 
/// [ARCHITECTURE PATTERN - "Idempotency as a Plugin"]:
/// Exactly-Once delivery is a distributed systems myth — you cannot guarantee it without
/// distributed transactions (2PC), which kill throughput.
///
/// The pragmatic solution is AT-LEAST-ONCE delivery + idempotent handlers:
///   - Level 1 (built-in): Deduplication via SQL UNIQUE INDEX on IdempotencyKey.
///     Prevents the same job from being ENQUEUED twice. This is free and always on.
///   - Level 2 (this store): Result caching. If a job already executed successfully,
///     return the cached result WITHOUT re-running the business logic.
///     This is expensive (extra storage), so it's opt-in for critical flows only
///     (e.g., payment processing, invoice generation).
///
/// This interface is intentionally minimal. Users provide their own implementation
/// backed by Redis, SQL, or any other store appropriate for their SLA.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Checks whether a job with this idempotency key has already been processed.
    /// Returns the cached result payload if present, null otherwise.
    /// </summary>
    /// <param name="idempotencyKey">The unique key for this operation (e.g., "payment:{orderId}").</param>
    ValueTask<string?> TryGetResultAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Stores the result of a successful execution.
    /// Subsequent calls with the same key will return this cached result.
    /// </summary>
    /// <param name="idempotencyKey">The unique key for this operation.</param>
    /// <param name="resultPayload">The serialized result to cache.</param>
    /// <param name="ttl">How long to keep this result. Null = store indefinitely.</param>
    ValueTask StoreResultAsync(string idempotencyKey, string resultPayload, TimeSpan? ttl = null, CancellationToken ct = default);
}
