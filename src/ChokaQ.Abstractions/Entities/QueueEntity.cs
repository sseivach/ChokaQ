namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents queue configuration in the Queues table.
/// Provides runtime control plane for operational management.
/// </summary>
/// <remarks>
/// Queues are auto-created on first job enqueue.
/// Configuration changes take effect immediately (no restart required).
/// </remarks>
/// <param name="Name">Unique queue identifier (e.g., "default", "mailing", "reports").</param>
/// <param name="IsPaused">When true, workers skip this queue during fetch operations.</param>
/// <param name="IsActive">When false, queue is hidden from dashboard (soft delete).</param>
/// <param name="ZombieTimeoutSeconds">Per-queue override for zombie detection. Null uses global setting.</param>
/// <param name="LastUpdatedUtc">Last configuration change timestamp.</param>
public record QueueEntity(
    string Name,
    bool IsPaused,
    bool IsActive,
    int? ZombieTimeoutSeconds,
    DateTime LastUpdatedUtc
);
