using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Resilience;
using System.Collections.Concurrent;

namespace ChokaQ.Core.Defaults;

/// <summary>
/// Thread-safe in-memory implementation of the Circuit Breaker pattern.
/// Uses a lock-free read / locked write approach for maximum performance.
/// 
/// [RESILIENCY PATTERN - "The Circuit State Machine"]:
/// 1. CLOSED: Normal operation. Fast lock-free reads.
/// 2. OPEN: External dependency is down. Fast fail to prevent thread pool exhaustion.
/// 3. HALF-OPEN: "Testing the waters". Allows exactly N requests to pass through 
///    to check if the dependency has recovered. If they fail, immediate trip back to OPEN.
///    This prevents the "Thundering Herd" on a newly recovered downstream service.
/// </summary>
public class InMemoryCircuitBreaker : ICircuitBreaker
{
    private readonly TimeProvider _timeProvider;
    private readonly CircuitPolicy _defaultPolicy = new();

    private readonly ConcurrentDictionary<string, CircuitPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, CircuitStateEntry> _states = new();

    public InMemoryCircuitBreaker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void RegisterPolicy(string circuitKey, CircuitPolicy policy)
    {
        _policies[circuitKey] = policy;
    }

    public bool IsExecutionPermitted(string circuitKey)
    {
        var entry = GetEntry(circuitKey);
        var now = _timeProvider.GetUtcNow();

        // FAST PATH: Lock-free read of the volatile Status field
        if (entry.Status == CircuitStatus.Closed) return true;

        return entry.TryAcquireExecutionPermit(now);
    }

    public void ReportSuccess(string circuitKey)
    {
        var entry = GetEntry(circuitKey);
        entry.RecordSuccess();
    }

    public void ReportFailure(string circuitKey, CircuitFailureSeverity severity = CircuitFailureSeverity.Transient)
    {
        var entry = GetEntry(circuitKey);
        var now = _timeProvider.GetUtcNow();

        entry.RecordFailure(now, severity);
    }

    public CircuitStatus GetStatus(string circuitKey) => GetEntry(circuitKey).Status;

    public IEnumerable<CircuitStatsDto> GetCircuitStats()
    {
        return _states.Select(kvp =>
        {
            var circuitKey = kvp.Key;
            var entry = kvp.Value;

            DateTime? resetAt = null;

            if (entry.Status == CircuitStatus.Open)
            {
                resetAt = entry.LastFailureUtc.AddSeconds(entry.Policy.BreakDurationSeconds).UtcDateTime;
            }

            return new CircuitStatsDto(circuitKey, entry.Status, entry.FailureCount, resetAt);
        });
    }

    private CircuitStateEntry GetEntry(string circuitKey)
    {
        return _states.GetOrAdd(circuitKey, key => 
        {
            var policy = _policies.GetValueOrDefault(key, _defaultPolicy);
            return new CircuitStateEntry(policy);
        });
    }

    /// <summary>
    /// Thread-safe container for circuit state.
    /// Mutations are protected by a micro-lock, while Status reads remain volatile and fast.
    /// </summary>
    private class CircuitStateEntry
    {
        public readonly CircuitPolicy Policy;
        private readonly object _stateLock = new();

        public volatile CircuitStatus Status = CircuitStatus.Closed;

        // These fields are mutated under lock, but can be safely read for UI stats
        public int FailureCount { get; private set; }
        public DateTimeOffset LastFailureUtc { get; private set; }
        public int ActiveHalfOpenCalls { get; private set; }

        public CircuitStateEntry(CircuitPolicy policy)
        {
            Policy = policy;
        }

        public void RecordSuccess()
        {
            // Lock-free check to avoid unnecessary locking overhead on the happy path
            if (Status == CircuitStatus.Closed && FailureCount == 0) return;

            lock (_stateLock)
            {
                Status = CircuitStatus.Closed;
                FailureCount = 0;
                ActiveHalfOpenCalls = 0;
            }
        }

        public void RecordFailure(DateTimeOffset now, CircuitFailureSeverity severity)
        {
            lock (_stateLock)
            {
                LastFailureUtc = now;
                FailureCount++;
                ActiveHalfOpenCalls = 0;

                if (Status == CircuitStatus.HalfOpen || FailureCount >= Policy.FailureThreshold || severity == CircuitFailureSeverity.Fatal)
                {
                    Status = CircuitStatus.Open;
                }
            }
        }

        public bool TryAcquireExecutionPermit(DateTimeOffset now)
        {
            lock (_stateLock)
            {
                if (Status == CircuitStatus.Open)
                {
                    // Check if break duration elapsed
                    if (now >= LastFailureUtc.AddSeconds(Policy.BreakDurationSeconds))
                    {
                        Status = CircuitStatus.HalfOpen;
                        ActiveHalfOpenCalls = 1;
                        return true;
                    }
                    return false;
                }

                if (Status == CircuitStatus.HalfOpen)
                {
                    if (ActiveHalfOpenCalls < Policy.HalfOpenMaxCalls)
                    {
                        ActiveHalfOpenCalls++;
                        return true;
                    }
                    return false; // Deny execution until half-open requests finish
                }

                return true; // Closed
            }
        }
    }
}