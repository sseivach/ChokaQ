using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation of the Circuit Breaker pattern.
/// Uses a lock-free read / locked write approach for maximum performance.
/// </summary>
public class InMemoryCircuitBreaker : ICircuitBreaker
{
    private readonly TimeProvider _timeProvider;

    // Configuration constants
    private const int FailureThreshold = 5;
    private const int BreakDurationSeconds = 30; // Circuit stays open for 30s

    private readonly ConcurrentDictionary<string, CircuitStateEntry> _states = new();

    public InMemoryCircuitBreaker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool IsExecutionPermitted(string jobType)
    {
        var entry = GetEntry(jobType);
        var now = _timeProvider.GetUtcNow();

        // FAST PATH: Lock-free read of the volatile Status field
        if (entry.Status == CircuitStatus.Closed) return true;

        if (entry.Status == CircuitStatus.Open)
        {
            // Check if timeout elapsed
            if (now >= entry.LastFailureUtc.AddSeconds(BreakDurationSeconds))
            {
                if (entry.TryTransitionToHalfOpen())
                {
                    return true;
                }
            }
            return false;
        }

        // Half-Open: Allow execution (could limit concurrency here in future)
        return true;
    }

    public void ReportSuccess(string jobType)
    {
        var entry = GetEntry(jobType);
        entry.RecordSuccess();
    }

    public void ReportFailure(string jobType)
    {
        var entry = GetEntry(jobType);
        var now = _timeProvider.GetUtcNow();

        entry.RecordFailure(now, FailureThreshold);
    }

    public CircuitStatus GetStatus(string jobType) => GetEntry(jobType).Status;

    /// <summary>
    /// Implementation of the new DTO-based stats method.
    /// </summary>
    public IEnumerable<CircuitStatsDto> GetCircuitStats()
    {
        return _states.Select(kvp =>
        {
            var jobType = kvp.Key;
            var entry = kvp.Value;

            DateTime? resetAt = null;

            // Calculate reset time if Open
            if (entry.Status == CircuitStatus.Open)
            {
                resetAt = entry.LastFailureUtc.AddSeconds(BreakDurationSeconds).UtcDateTime;
            }

            return new CircuitStatsDto(jobType, entry.Status, entry.FailureCount, resetAt);
        });
    }

    private CircuitStateEntry GetEntry(string jobType)
    {
        return _states.GetOrAdd(jobType, _ => new CircuitStateEntry());
    }

    /// <summary>
    /// Thread-safe container for circuit state.
    /// Mutations are protected by a micro-lock, while Status reads remain volatile and fast.
    /// </summary>
    private class CircuitStateEntry
    {
        private readonly object _stateLock = new();

        public volatile CircuitStatus Status = CircuitStatus.Closed;

        // These fields are mutated under lock, but can be safely read for UI stats
        public int FailureCount { get; private set; }
        public DateTimeOffset LastFailureUtc { get; private set; }

        public void RecordSuccess()
        {
            // Lock-free check to avoid unnecessary locking overhead on the happy path
            if (Status == CircuitStatus.Closed && FailureCount == 0) return;

            lock (_stateLock)
            {
                Status = CircuitStatus.Closed;
                FailureCount = 0;
            }
        }

        public void RecordFailure(DateTimeOffset now, int threshold)
        {
            lock (_stateLock)
            {
                LastFailureUtc = now;
                FailureCount++;

                if (Status == CircuitStatus.HalfOpen || FailureCount >= threshold)
                {
                    Status = CircuitStatus.Open;
                }
            }
        }

        public bool TryTransitionToHalfOpen()
        {
            lock (_stateLock)
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
}