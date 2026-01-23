namespace ChokaQ.Core.Defaults;

public class InMemoryStorageOptions
{
    /// <summary>
    /// The maximum number of jobs to keep in memory.
    /// When this limit is reached, the storage will attempt to remove finished jobs first, 
    /// then the oldest jobs, to prevent OutOfMemory exceptions.
    /// Default: 100,000.
    /// </summary>
    public int MaxCapacity { get; set; } = 100_000;
}