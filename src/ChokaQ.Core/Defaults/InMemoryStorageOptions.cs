namespace ChokaQ.Core.Defaults;

public class InMemoryStorageOptions
{
    /// <summary>
    /// Soft cap for jobs retained by the in-memory Three Pillars store.
    /// When this limit is reached, storage evicts old Archive rows first and old DLQ rows second,
    /// while preserving Hot rows so accepted work is not silently lost.
    /// Default: 100,000.
    /// </summary>
    /// <remarks>
    /// This is an in-process safety valve, not a production durability guarantee. In-memory mode
    /// is best for local development, demos, tests, and high-throughput volatile streams. SQL mode
    /// should be used when admission control must survive process restarts or multiple instances.
    /// </remarks>
    public int MaxCapacity { get; set; } = 100_000;
}
