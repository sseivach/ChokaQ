namespace ChokaQ.Abstractions.Entities;

/// <summary>
/// QUEUE CONFIGURATION. Represents a record in the [Queues] table.
/// Acts as the control plane for individual queue behavior.
/// </summary>
public record QueueEntity(
    string Name,                // Unique queue name (Primary Key)
    bool IsPaused,              // Flag: 1=Workers stop fetching
    bool IsActive,              // Flag: 0=Hide from monitoring
    DateTime LastUpdatedUtc,    // Audit timestamp for configuration changes
    int? ZombieTimeoutSeconds   // Custom heartbeat threshold (Seconds)
);