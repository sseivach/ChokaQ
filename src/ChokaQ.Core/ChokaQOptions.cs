using ChokaQ.Abstractions.Jobs;
using ChokaQ.Abstractions.Middleware;
using ChokaQ.Abstractions.Observability;
using ChokaQ.Core.Defaults;

namespace ChokaQ.Core;

/// <summary>
/// Configuration options for the ChokaQ library.
/// Allows selecting the processing strategy (Bus vs Pipe) and configuring runtime defaults.
/// </summary>
public class ChokaQOptions
{
    /// <summary>
    /// Conventional configuration section name used by the IConfiguration overload.
    /// Host applications can bind either this section or pass the section itself.
    /// </summary>
    public const string SectionName = "ChokaQ";

    // --- Strategy State (Internal use) ---

    internal bool IsPipeMode { get; private set; }
    internal Type? PipeHandlerType { get; private set; }
    internal List<Type> ProfileTypes { get; } = new();
    internal List<Type> MiddlewareTypes { get; } = new();

    // --- Storage Configuration ---

    /// <summary>
    /// Holds configuration for the default In-Memory storage.
    /// Access via ConfigureInMemory() method.
    /// </summary>
    public InMemoryStorageOptions InMemoryOptions { get; } = new();

    /// <summary>
    /// Configuration alias used by appsettings binding: ChokaQ:InMemory.
    /// </summary>
    /// <remarks>
    /// The older code API exposes InMemoryOptions because it was originally only configured
    /// through ConfigureInMemory(). NuGet consumers tend to read JSON, not source history, so
    /// the shorter InMemory section gives operators a clean configuration name while both
    /// properties still point at the same options object.
    /// </remarks>
    public InMemoryStorageOptions InMemory => InMemoryOptions;

    // --- Runtime Configuration ---

    /// <summary>
    /// Execution-related safety knobs: handler timeout and heartbeat behavior.
    /// </summary>
    public ChokaQExecutionOptions Execution { get; set; } = new();

    /// <summary>
    /// Retry and backoff policy used after retryable handler failures.
    /// </summary>
    public ChokaQRetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Recovery policy used by ZombieRescueService for abandoned and heartbeat-expired jobs.
    /// </summary>
    public ChokaQRecoveryOptions Recovery { get; set; } = new();

    /// <summary>
    /// Worker-loop timing knobs that are not tied to SQL Server polling.
    /// </summary>
    public ChokaQWorkerOptions Worker { get; set; } = new();

    /// <summary>
    /// OpenTelemetry metric contract and tag-cardinality controls.
    /// </summary>
    /// <remarks>
    /// Metrics look harmless until an unbounded tag value becomes a storage bill or takes down
    /// Prometheus. ChokaQ keeps the instrument names stable and lets hosts cap the number of
    /// distinct queue/type/error/reason values emitted by one process.
    /// </remarks>
    public ChokaQMetricsOptions Metrics { get; set; } = new();

    /// <summary>
    /// Per-queue runtime overrides.
    /// </summary>
    /// <remarks>
    /// Queue-specific overrides are intentionally sparse. Global defaults should carry most
    /// installations; per-queue values are for real workload differences such as long-running
    /// report generation versus short HTTP callback jobs.
    /// </remarks>
    public Dictionary<string, ChokaQQueueRuntimeOptions> Queues { get; set; } = new(StringComparer.OrdinalIgnoreCase);


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
    /// Registers a middleware to intercept job execution.
    /// Middlewares are executed in the order they are added (Pipeline pattern).
    /// </summary>
    /// <typeparam name="TMiddleware">The middleware type.</typeparam>
    public void AddMiddleware<TMiddleware>() where TMiddleware : class, IChokaQMiddleware
    {
        MiddlewareTypes.Add(typeof(TMiddleware));
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
    /// Backward-compatible alias for Retry.MaxAttempts.
    /// </summary>
    /// <remarks>
    /// The historical property name says "retries", but the current engine interprets this
    /// number as total execution attempts, including the first try. Keeping the alias avoids
    /// breaking existing hosts while the nested Retry section becomes the preferred API.
    /// </remarks>
    public int MaxRetries
    {
        get => Retry.MaxAttempts;
        set => Retry.MaxAttempts = value;
    }

    /// <summary>
    /// Backward-compatible alias for Retry.BaseDelay, expressed in seconds.
    /// </summary>
    public int RetryDelaySeconds
    {
        get => (int)Retry.BaseDelay.TotalSeconds;
        set => Retry.BaseDelay = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Backward-compatible alias for Recovery.ProcessingZombieTimeout, expressed in seconds.
    /// </summary>
    public int ZombieTimeoutSeconds
    {
        get => (int)Recovery.ProcessingZombieTimeout.TotalSeconds;
        set => Recovery.ProcessingZombieTimeout = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Backward-compatible alias for Recovery.FetchedJobTimeout, expressed in seconds.
    /// </summary>
    /// <remarks>
    /// Fetched and Processing are intentionally separate states with different risk profiles.
    /// A Fetched job has not executed user code yet, so recovery is safe; a Processing job may
    /// have already performed side effects and must be moved to DLQ when its heartbeat expires.
    /// Keeping this timeout independent prevents large prefetch buffers from being reclaimed
    /// just because an application configured a short Processing zombie timeout.
    /// </remarks>
    public int FetchedJobTimeoutSeconds
    {
        get => (int)Recovery.FetchedJobTimeout.TotalSeconds;
        set => Recovery.FetchedJobTimeout = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Resolves the execution timeout for a queue, falling back to the global default.
    /// </summary>
    internal TimeSpan GetExecutionTimeoutForQueue(string queueName)
    {
        if (!string.IsNullOrWhiteSpace(queueName)
            && Queues.TryGetValue(queueName, out var queueOptions)
            && queueOptions.ExecutionTimeout.HasValue)
        {
            return queueOptions.ExecutionTimeout.Value;
        }

        return Execution.DefaultTimeout;
    }

    /// <summary>
    /// Throws a startup exception when the runtime configuration is unsafe or ambiguous.
    /// </summary>
    /// <remarks>
    /// ChokaQ is a background processor, so silent configuration mistakes are expensive:
    /// a zero timeout can instantly DLQ every job, a negative retry delay can spin the CPU,
    /// and an inverted heartbeat range can hide zombie detection bugs. Validation happens
    /// during service registration so the host fails before it accepts traffic.
    /// </remarks>
    public void ValidateOrThrow()
    {
        var errors = Validate();

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid ChokaQ configuration: " + string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Returns all validation errors for diagnostics and tests.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        RequirePositive(Execution.DefaultTimeout, "Execution.DefaultTimeout", errors);
        RequirePositive(Execution.HeartbeatIntervalMin, "Execution.HeartbeatIntervalMin", errors);
        RequirePositive(Execution.HeartbeatIntervalMax, "Execution.HeartbeatIntervalMax", errors);

        if (Execution.HeartbeatIntervalMax < Execution.HeartbeatIntervalMin)
        {
            errors.Add("Execution.HeartbeatIntervalMax must be greater than or equal to Execution.HeartbeatIntervalMin.");
        }

        if (Execution.HeartbeatFailureThreshold < 1)
        {
            errors.Add("Execution.HeartbeatFailureThreshold must be at least 1.");
        }

        if (Retry.MaxAttempts < 1)
        {
            errors.Add("Retry.MaxAttempts must be at least 1.");
        }

        RequirePositive(Retry.BaseDelay, "Retry.BaseDelay", errors);
        RequirePositive(Retry.MaxDelay, "Retry.MaxDelay", errors);
        RequirePositive(Retry.CircuitBreakerDelay, "Retry.CircuitBreakerDelay", errors);
        RequireNotNegative(Retry.JitterMaxDelay, "Retry.JitterMaxDelay", errors);

        if (Retry.BackoffMultiplier < 1)
        {
            errors.Add("Retry.BackoffMultiplier must be greater than or equal to 1.");
        }

        if (Retry.MaxDelay < Retry.BaseDelay)
        {
            errors.Add("Retry.MaxDelay must be greater than or equal to Retry.BaseDelay.");
        }

        if (Retry.MaxDelay.TotalMilliseconds > int.MaxValue)
        {
            errors.Add("Retry.MaxDelay must not exceed int.MaxValue milliseconds until scheduler delays are widened.");
        }

        RequirePositive(Recovery.FetchedJobTimeout, "Recovery.FetchedJobTimeout", errors);
        RequirePositive(Recovery.ProcessingZombieTimeout, "Recovery.ProcessingZombieTimeout", errors);
        RequirePositive(Recovery.ScanInterval, "Recovery.ScanInterval", errors);
        RequirePositive(Worker.PausedQueuePollingDelay, "Worker.PausedQueuePollingDelay", errors);
        RequirePositive(InMemoryOptions.MaxCapacity, "InMemory.MaxCapacity", errors);
        errors.AddRange(Metrics.Validate());

        foreach (var (queueName, queueOptions) in Queues)
        {
            if (string.IsNullOrWhiteSpace(queueName))
            {
                errors.Add("Queues contains an empty queue name.");
            }

            if (queueOptions.ExecutionTimeout is { } executionTimeout)
            {
                RequirePositive(executionTimeout, $"Queues[{queueName}].ExecutionTimeout", errors);
            }
        }

        return errors;
    }

    private static void RequirePositive(TimeSpan value, string name, ICollection<string> errors)
    {
        if (value <= TimeSpan.Zero)
        {
            errors.Add($"{name} must be greater than zero.");
        }
    }

    private static void RequirePositive(int value, string name, ICollection<string> errors)
    {
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than zero.");
        }
    }

    private static void RequireNotNegative(TimeSpan value, string name, ICollection<string> errors)
    {
        if (value < TimeSpan.Zero)
        {
            errors.Add($"{name} must not be negative.");
        }
    }
}

/// <summary>
/// Handler execution safety limits.
/// </summary>
public sealed class ChokaQExecutionOptions
{
    /// <summary>
    /// Maximum wall-clock time a job handler may run before ChokaQ cancels it.
    /// Default: 15 minutes.
    /// </summary>
    /// <remarks>
    /// This is a guardrail, not a business SLA. Long-running handlers should either increase
    /// this value explicitly or split work into smaller jobs so cancellation/retry semantics
    /// remain understandable.
    /// </remarks>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Lower bound for heartbeat delay jitter. Default: 8 seconds.
    /// </summary>
    public TimeSpan HeartbeatIntervalMin { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Upper bound for heartbeat delay jitter. Default: 12 seconds.
    /// </summary>
    public TimeSpan HeartbeatIntervalMax { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Consecutive heartbeat write failures allowed before the running job is cancelled.
    /// Default: 3.
    /// </summary>
    public int HeartbeatFailureThreshold { get; set; } = 3;
}

/// <summary>
/// Retry policy for transient handler failures.
/// </summary>
public sealed class ChokaQRetryOptions
{
    /// <summary>
    /// Maximum total execution attempts, including the first try. Default: 3.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// First retry delay before exponential growth is applied. Default: 3 seconds.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Hard upper bound for calculated retry delay. Default: 1 hour.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Exponential multiplier applied after each attempt. Default: 2.0.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Maximum random delay added to retry scheduling to avoid synchronized retry waves.
    /// Default: 1 second.
    /// </summary>
    public TimeSpan JitterMaxDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Delay used when the circuit breaker blocks execution. Default: 5 seconds.
    /// </summary>
    public TimeSpan CircuitBreakerDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Recovery policy for jobs that were claimed by a worker but stopped making progress.
/// </summary>
public sealed class ChokaQRecoveryOptions
{
    /// <summary>
    /// Timeout before a Fetched job is considered abandoned and returned to Pending.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan FetchedJobTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Timeout before a Processing job with an expired heartbeat is moved to DLQ as a zombie.
    /// Default: 10 minutes.
    /// </summary>
    public TimeSpan ProcessingZombieTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often ZombieRescueService scans for abandoned and zombie jobs. Default: 1 minute.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Worker-loop timing knobs shared by in-memory mode.
/// </summary>
public sealed class ChokaQWorkerOptions
{
    /// <summary>
    /// Delay used by the in-memory worker before rechecking a paused queue. Default: 1 second.
    /// </summary>
    public TimeSpan PausedQueuePollingDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Runtime overrides for a specific queue.
/// </summary>
public sealed class ChokaQQueueRuntimeOptions
{
    /// <summary>
    /// Optional handler execution timeout for this queue.
    /// </summary>
    /// <remarks>
    /// Use this when one queue hosts naturally long jobs, such as reports or exports, while
    /// the rest of the system should retain a shorter fail-fast timeout.
    /// </remarks>
    public TimeSpan? ExecutionTimeout { get; set; }
}
