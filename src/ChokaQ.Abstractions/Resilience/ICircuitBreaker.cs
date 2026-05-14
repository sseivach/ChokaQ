using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions.Resilience;

/// <summary>
/// Defines a circuit breaker to protect external resources from overload.
/// 
/// [RESILIENCY PATTERN - "Bulkhead Isolation"]:
/// Per-circuitKey isolation ensures that a failure in one external dependency 
/// (e.g., a specific Payment API) does not saturate the worker pool and 
/// crash the entire system. Other queues and job types continue to process.
/// 
/// [RESILIENCY PATTERN - "Fail-Fast"]:
/// When the circuit is OPEN, IsExecutionPermitted returns false immediately.
/// The JobProcessor then fails the job with a transient error, freeing the 
/// worker to take another job instead of waiting for a guaranteed timeout.
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// Registers a specific policy for a circuit key.
    /// If not registered, a default policy is used.
    /// </summary>
    void RegisterPolicy(string circuitKey, CircuitPolicy policy);

    /// <summary>
    /// Checks if the execution of a specific circuit key is permitted.
    /// </summary>
    /// <param name="circuitKey">The key identifying the external dependency or job type.</param>
    bool IsExecutionPermitted(string circuitKey);

    /// <summary>
    /// Releases a previously permitted execution that never produced a success or failure result.
    /// </summary>
    /// <remarks>
    /// This is mainly for HalfOpen probes. A worker can acquire permission and then skip
    /// handler execution because the job lease was stale, the host shut down, or an admin
    /// cancelled the job before dispatch. Releasing the permit prevents the circuit from
    /// staying stuck in HalfOpen.
    /// </remarks>
    void ReleaseExecutionPermit(string circuitKey);

    /// <summary>
    /// Reports a successful execution, resetting failure counters.
    /// </summary>
    void ReportSuccess(string circuitKey);

    /// <summary>
    /// Reports a failure, potentially opening the circuit based on severity.
    /// </summary>
    void ReportFailure(string circuitKey, CircuitFailureSeverity severity = CircuitFailureSeverity.Transient);

    /// <summary>
    /// Gets the current status of the circuit for monitoring purposes.
    /// </summary>
    CircuitStatus GetStatus(string circuitKey);

    /// <summary>
    /// Retrieves the status of all tracked circuits.
    /// Used for monitoring/dashboard.
    /// </summary>
    IEnumerable<CircuitStatsDto> GetCircuitStats();
}

/// <summary>
/// Optional lease-aware circuit API for components that can keep permit ownership until outcome.
/// </summary>
public interface ICircuitBreakerLeaseProvider
{
    /// <summary>
    /// Attempts to acquire an execution permit and returns a lease that owns the eventual outcome.
    /// </summary>
    ICircuitBreakerExecutionLease? TryAcquireExecutionPermit(string circuitKey);
}

/// <summary>
/// Owns one circuit execution permit until success, failure, or release.
/// </summary>
public interface ICircuitBreakerExecutionLease : IDisposable
{
    string CircuitKey { get; }

    void ReportSuccess();

    void ReportFailure(CircuitFailureSeverity severity = CircuitFailureSeverity.Transient);

    void Release();
}
