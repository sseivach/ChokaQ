using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;

namespace ChokaQ.Abstractions;

public interface ICircuitBreaker
{
    /// <summary>
    /// Checks if the execution of a specific job type is permitted.
    /// </summary>
    /// <param name="jobType">The key identifying the job type.</param>
    bool IsExecutionPermitted(string jobType);

    /// <summary>
    /// Reports a successful execution, resetting failure counters.
    /// </summary>
    void ReportSuccess(string jobType);

    /// <summary>
    /// Reports a failure, potentially opening the circuit.
    /// </summary>
    void ReportFailure(string jobType);

    /// <summary>
    /// Gets the current status of the circuit for monitoring purposes.
    /// </summary>
    CircuitStatus GetStatus(string jobType);

    /// <summary>
    /// Retrieves the status of all tracked circuits (Job Types).
    /// Used for monitoring/dashboard.
    /// </summary>
    IEnumerable<CircuitStatsDto> GetCircuitStats();
}