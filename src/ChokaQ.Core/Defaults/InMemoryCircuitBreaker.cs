using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

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
        if (entry.Status != CircuitStatus.Closed)
        {
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
            entry.Status = CircuitStatus.Open;
        }
        else if (entry.FailureCount >= FailureThreshold)
        {
            entry.Status = CircuitStatus.Open;
        }
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

    private class CircuitStateEntry
    {
        public volatile CircuitStatus Status = CircuitStatus.Closed;
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