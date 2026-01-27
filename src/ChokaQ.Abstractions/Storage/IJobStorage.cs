using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

/// <summary>
/// Defines the contract for job persistence providers.
/// Supports Pluggable Provider Model (Strategy Pattern).
/// </summary>
public interface IJobStorage
{
    /// <summary>
    /// Persists a new job into the storage.
    /// </summary>
    ValueTask<string> CreateJobAsync(
        string id,
        string queue,
        string jobType,
        string payload,
        int priority = 10,
        string? createdBy = null,
        string? tags = null,
        TimeSpan? delay = null,
        string? idempotencyKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the list of all active queues and their stats.
    /// </summary>
    ValueTask<IEnumerable<QueueDto>> GetQueuesAsync(CancellationToken ct = default);

    /// <summary>
    /// Pauses or Resumes a specific queue.
    /// </summary>
    ValueTask SetQueueStateAsync(string queueName, bool isPaused, CancellationToken ct = default);

    /// <summary>
    /// Worker heartbeat. Light update to confirm "I am alive".
    /// </summary>
    ValueTask KeepAliveAsync(string jobId, CancellationToken ct = default);

    /// <summary>
    /// Scans for "Zombie" jobs (Processing state + expired heartbeat) and marks them as Zombie (6).
    /// </summary>
    /// <param name="globalTimeoutSeconds">Fallback timeout if queue specific setting is null.</param>
    /// <returns>Count of zombies found.</returns>
    ValueTask<int> MarkZombiesAsync(int globalTimeoutSeconds, CancellationToken ct = default);

    /// <summary>
    /// Updates the Zombie Timeout configuration for a specific queue.
    /// </summary>
    ValueTask UpdateQueueTimeoutAsync(string queueName, int? timeoutSeconds, CancellationToken ct = default);
}