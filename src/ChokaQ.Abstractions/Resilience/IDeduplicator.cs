namespace ChokaQ.Abstractions.Resilience;

/// <summary>
/// Defines a contract for a lightweight deduplication mechanism.
/// Used to prevent "retry storms" or double-clicks from hitting the database.
/// </summary>
public interface IDeduplicator
{
    /// <summary>
    /// Attempts to acquire a lock for a specific key for a given duration.
    /// </summary>
    /// <param name="key">The unique idempotency key.</param>
    /// <param name="ttl">Time To Live. How long the key should be remembered.</param>
    /// <returns>
    /// True if the lock was acquired (new key).
    /// False if the key is already locked/busy (duplicate).
    /// </returns>
    ValueTask<bool> TryAcquireAsync(string key, TimeSpan ttl);
}