namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// Represents queue configuration in the Queues table.
/// Runtime control plane for circuit breakers and visibility settings.
/// </summary>
public record QueueEntity(
    string Name,
    bool IsPaused,
    bool IsActive,
    int? ZombieTimeoutSeconds,
    DateTime LastUpdatedUtc
);
