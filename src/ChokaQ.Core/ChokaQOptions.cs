using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core.Defaults;

namespace ChokaQ.Core;

/// <summary>
/// Configuration options for the ChokaQ library.
/// Allows selecting the processing strategy (Bus vs Pipe) and configuring storage defaults.
/// </summary>
public class ChokaQOptions
{
    // --- Strategy State (Internal use) ---

    internal bool IsPipeMode { get; private set; }
    internal Type? PipeHandlerType { get; private set; }
    internal List<Type> ProfileTypes { get; } = new();

    // --- Storage Configuration ---

    /// <summary>
    /// Holds configuration for the default In-Memory storage.
    /// Access via ConfigureInMemory() method.
    /// </summary>
    public InMemoryStorageOptions InMemoryOptions { get; } = new();

    // --- Public API ---

    /// <summary>
    /// Activates "Pipe Mode". 
    /// In this mode, all jobs are treated as raw data and routed to a single Global Handler.
    /// Ideal for high-throughput scenarios or simple event streams.
    /// </summary>
    /// <typeparam name="THandler">The global handler that will process all incoming messages.</typeparam>
    public void UsePipe<THandler>() where THandler : IChokaQPipeHandler
    {
        IsPipeMode = true;
        PipeHandlerType = typeof(THandler);
    }

    /// <summary>
    /// Adds a Job Profile (Bus Mode).
    /// Registers a set of Job Types and their specific Handlers.
    /// </summary>
    /// <typeparam name="TProfile">The profile class containing job registrations.</typeparam>
    public void AddProfile<TProfile>() where TProfile : ChokaQJobProfile
    {
        ProfileTypes.Add(typeof(TProfile));
    }

    /// <summary>
    /// Configures the default In-Memory storage behavior.
    /// Useful for Pipe Mode where no persistent database is used.
    /// </summary>
    /// <param name="configure">Action to configure options (e.g., MaxCapacity).</param>
    public void ConfigureInMemory(Action<InMemoryStorageOptions> configure)
    {
        configure(InMemoryOptions);
    }

    /// <summary>
    /// Default maximum retries for failed jobs. Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base retry delay in seconds (Exponential Backoff starts here). Default: 3.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Default timeout in seconds before a processing job without heartbeats is considered a zombie.
    /// This value is used if a queue does not have a specific timeout configured.
    /// Default: 600 seconds (10 minutes).
    /// </summary>
    public int ZombieTimeoutSeconds { get; set; } = 600;
}