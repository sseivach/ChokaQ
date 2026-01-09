using ChokaQ.Abstractions;
using ChokaQ.Abstractions.Enums;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

public class InMemoryCircuitBreaker : ICircuitBreaker
{
    private readonly TimeProvider _timeProvider;

    // Configuration constants (could be moved to appsettings later)
    private const int FailureThreshold = 5; // Number of consecutive failures to open the circuit
    private const int BreakDurationSeconds = 30; // Duration to keep the circuit open

    private readonly ConcurrentDictionary<string, CircuitStateEntry> _states = new();

    public InMemoryCircuitBreaker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsExecutionPermitted(string jobType)
    {
        var entry = GetEntry(jobType);
        var now = _timeProvider.GetUtcNow();

        // 1. Closed: Business as usual.
        if (entry.Status == CircuitStatus.Closed) return true;

        // 2. Open: Check if the timeout has elapsed.
        if (entry.Status == CircuitStatus.Open)
        {
            if (now >= entry.LastFailureUtc.AddSeconds(BreakDurationSeconds))
            {
                // Timeout elapsed, try to transition to Half-Open.
                // Using TryTransition to ensure atomicity.
                if (entry.TryTransitionToHalfOpen())
                {
                    return true;
                }
            }
            return false; // Still open, block execution.
        }

        // 3. Half-Open: Allow execution.
        // In a more complex implementation, we might want to limit concurrency here.
        return true;
    }

    public void ReportSuccess(string jobType)
    {
        var entry = GetEntry(jobType);
        if (entry.Status != CircuitStatus.Closed)
        {
            // Success! The external service recovered. Reset everything.
            entry.Reset();
        }
    }

    public void ReportFailure(string jobType)
    {
        var entry = GetEntry(jobType);
        var now = _timeProvider.GetUtcNow();

        entry.LastFailureUtc = now;
        entry.FailureCount++;

        if (entry.Status == CircuitStatus.HalfOpen)
        {
            // Failed during trial. Immediately re-open the circuit.
            entry.Status = CircuitStatus.Open;
        }
        else if (entry.FailureCount >= FailureThreshold)
        {
            // Failure threshold reached. Open the circuit.
            entry.Status = CircuitStatus.Open;
        }
    }

    public CircuitStatus GetStatus(string jobType) => GetEntry(jobType).Status;

    /// <summary>
    /// Returns a snapshot of current states.
    /// </summary>
    public IReadOnlyDictionary<string, CircuitStatus> GetCircuitStates()
    {
        // Project the internal Entry objects to a simple Dictionary<string, Status>
        return _states.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Status
        );
    }

    private CircuitStateEntry GetEntry(string jobType)
    {
        return _states.GetOrAdd(jobType, _ => new CircuitStateEntry());
    }

    // Inner class to hold the state
    private class CircuitStateEntry
    {
        public volatile CircuitStatus Status = CircuitStatus.Closed; // Volatile for thread safety
        public int FailureCount;
        public DateTimeOffset LastFailureUtc;

        public void Reset()
        {
            Status = CircuitStatus.Closed;
            FailureCount = 0;
        }

        public bool TryTransitionToHalfOpen()
        {
            if (Status == CircuitStatus.Open)
            {
                Status = CircuitStatus.HalfOpen;
                return true;
            }
            return false;
        }
    }
}