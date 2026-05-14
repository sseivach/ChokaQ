using ChokaQ.Abstractions.DTOs;
using ChokaQ.Abstractions.Enums;
using ChokaQ.Abstractions.Observability;
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
internal class InMemoryCircuitBreaker : ICircuitBreaker, ICircuitBreakerLeaseProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly IChokaQMetrics? _metrics;
    private readonly CircuitPolicy _defaultPolicy = new();

    private readonly ConcurrentDictionary<string, CircuitPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, CircuitStateEntry> _states = new();

    public InMemoryCircuitBreaker(TimeProvider timeProvider, IChokaQMetrics? metrics = null)
    {
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    public void RegisterPolicy(string circuitKey, CircuitPolicy policy)
    {
        if (policy.FailureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "FailureThreshold must be at least 1.");
        }

        if (policy.BreakDurationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "BreakDurationSeconds must be at least 1.");
        }

        if (policy.HalfOpenMaxCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "HalfOpenMaxCalls must be at least 1.");
        }

        if (policy.HalfOpenProbeTimeoutSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "HalfOpenProbeTimeoutSeconds must be at least 1.");
        }

        if (policy.FailureWindowSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "FailureWindowSeconds must be at least 1.");
        }

        _policies[circuitKey] = policy;
    }

    public bool IsExecutionPermitted(string circuitKey)
    {
        return TryAcquireExecutionPermit(circuitKey) is not null;
    }

    public ICircuitBreakerExecutionLease? TryAcquireExecutionPermit(string circuitKey)
    {
        var entry = GetEntry(circuitKey);
        var now = _timeProvider.GetUtcNow();
        return entry.TryAcquireExecutionPermit(circuitKey, now, _timeProvider);
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

    public void ReleaseExecutionPermit(string circuitKey)
    {
        var entry = GetEntry(circuitKey);
        entry.ReleaseExecutionPermit(_timeProvider.GetUtcNow());
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
            return new CircuitStateEntry(key, policy, _metrics);
        });
    }

    /// <summary>
    /// Thread-safe container for circuit state.
    /// Mutations are protected by a micro-lock, while Status reads remain volatile and fast.
    /// </summary>
    private class CircuitStateEntry
    {
        public readonly CircuitPolicy Policy;
        private readonly string _circuitKey;
        private readonly IChokaQMetrics? _metrics;
        private readonly object _stateLock = new();

        public volatile CircuitStatus Status = CircuitStatus.Closed;

        // These fields are mutated under lock, but can be safely read for UI stats
        public int FailureCount { get; private set; }
        public DateTimeOffset LastFailureUtc { get; private set; }
        public int ActiveHalfOpenCalls { get; private set; }
        public DateTimeOffset HalfOpenStartedUtc { get; private set; }
        public int SuccessfulHalfOpenCalls { get; private set; }

        public CircuitStateEntry(string circuitKey, CircuitPolicy policy, IChokaQMetrics? metrics)
        {
            _circuitKey = circuitKey;
            Policy = policy;
            _metrics = metrics;
        }

        public void RecordSuccess()
        {
            // Lock-free check to avoid unnecessary locking overhead on the happy path
            if (Status == CircuitStatus.Closed && FailureCount == 0) return;

            lock (_stateLock)
            {
                    if (Status == CircuitStatus.HalfOpen)
                    {
                        RecordHalfOpenSuccess();
                    if (Status == CircuitStatus.HalfOpen) return;
                }

                Close();
            }
        }

        public void RecordFailure(DateTimeOffset now, CircuitFailureSeverity severity)
        {
            lock (_stateLock)
            {
                if (Status == CircuitStatus.Closed
                    && FailureCount > 0
                    && now >= LastFailureUtc.AddSeconds(Policy.FailureWindowSeconds))
                {
                    FailureCount = 0;
                }

                LastFailureUtc = now;
                FailureCount++;

                if (Status == CircuitStatus.HalfOpen || FailureCount >= Policy.FailureThreshold || severity == CircuitFailureSeverity.Fatal)
                {
                    Open();
                }
            }
        }

        public ICircuitBreakerExecutionLease? TryAcquireExecutionPermit(
            string circuitKey,
            DateTimeOffset now,
            TimeProvider timeProvider)
        {
            // FAST PATH: lock-free read of the volatile status for closed circuits.
            if (Status == CircuitStatus.Closed)
            {
                return new CircuitBreakerExecutionLease(this, circuitKey, CircuitStatus.Closed, 0, timeProvider);
            }

            lock (_stateLock)
            {
                if (Status == CircuitStatus.Open)
                {
                    // Check if break duration elapsed
                    if (now >= LastFailureUtc.AddSeconds(Policy.BreakDurationSeconds))
                    {
                        BeginHalfOpen(now);
                        ActiveHalfOpenCalls++;
                        _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "probe_acquired");
                        return new CircuitBreakerExecutionLease(
                            this,
                            circuitKey,
                            CircuitStatus.HalfOpen,
                            _halfOpenGeneration,
                            timeProvider);
                    }
                    _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.Open.ToString(), "rejected");
                    return null;
                }

                if (Status == CircuitStatus.HalfOpen)
                {
                    ExpireStaleHalfOpenPermits(now);

                    if (ActiveHalfOpenCalls < Policy.HalfOpenMaxCalls)
                    {
                        ActiveHalfOpenCalls++;
                        _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "probe_acquired");
                        return new CircuitBreakerExecutionLease(
                            this,
                            circuitKey,
                            CircuitStatus.HalfOpen,
                            _halfOpenGeneration,
                            timeProvider);
                    }
                    _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "rejected");
                    return null; // Deny execution until half-open requests finish
                }

                return new CircuitBreakerExecutionLease(this, circuitKey, CircuitStatus.Closed, 0, timeProvider);
            }
        }

        public void ReleaseExecutionPermit(DateTimeOffset now)
        {
            lock (_stateLock)
            {
                if (Status != CircuitStatus.HalfOpen)
                {
                    return;
                }

                ExpireStaleHalfOpenPermits(now);
                if (ActiveHalfOpenCalls > 0)
                {
                    ActiveHalfOpenCalls--;
                    _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "permit_released");
                }
            }
        }

        private int _halfOpenGeneration;

        private void ReportLeaseSuccess(CircuitStatus acquiredStatus, int halfOpenGeneration)
        {
            if (acquiredStatus == CircuitStatus.Closed)
            {
                RecordSuccess();
                return;
            }

            lock (_stateLock)
            {
                if (Status != CircuitStatus.HalfOpen || _halfOpenGeneration != halfOpenGeneration)
                {
                    return;
                }

                RecordHalfOpenSuccess();
            }
        }

        private void ReportLeaseFailure(
            CircuitStatus acquiredStatus,
            int halfOpenGeneration,
            DateTimeOffset now,
            CircuitFailureSeverity severity)
        {
            if (acquiredStatus == CircuitStatus.Closed)
            {
                RecordFailure(now, severity);
                return;
            }

            lock (_stateLock)
            {
                if (Status != CircuitStatus.HalfOpen || _halfOpenGeneration != halfOpenGeneration)
                {
                    return;
                }

                LastFailureUtc = now;
                FailureCount++;
                Open();
            }
        }

        private void ReleaseLease(CircuitStatus acquiredStatus, int halfOpenGeneration, DateTimeOffset now)
        {
            if (acquiredStatus == CircuitStatus.Closed)
            {
                return;
            }

            lock (_stateLock)
            {
                if (Status != CircuitStatus.HalfOpen || _halfOpenGeneration != halfOpenGeneration)
                {
                    return;
                }

                ExpireStaleHalfOpenPermits(now);
                if (Status == CircuitStatus.HalfOpen
                    && _halfOpenGeneration == halfOpenGeneration
                    && ActiveHalfOpenCalls > 0)
                {
                    ActiveHalfOpenCalls--;
                    _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "permit_released");
                }
            }
        }

        private void RecordHalfOpenSuccess()
        {
            if (ActiveHalfOpenCalls > 0)
            {
                ActiveHalfOpenCalls--;
            }

            SuccessfulHalfOpenCalls++;
            if (SuccessfulHalfOpenCalls >= Policy.HalfOpenMaxCalls)
            {
                Close();
            }
        }

        private void ExpireStaleHalfOpenPermits(DateTimeOffset now)
        {
            if (Status != CircuitStatus.HalfOpen || ActiveHalfOpenCalls == 0)
            {
                return;
            }

            if (now < HalfOpenStartedUtc.AddSeconds(Policy.HalfOpenProbeTimeoutSeconds))
            {
                return;
            }

            ActiveHalfOpenCalls = 0;
            SuccessfulHalfOpenCalls = 0;
            HalfOpenStartedUtc = now;
            _halfOpenGeneration++;
            _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "probe_expired");
        }

        private void BeginHalfOpen(DateTimeOffset now)
        {
            Status = CircuitStatus.HalfOpen;
            ActiveHalfOpenCalls = 0;
            SuccessfulHalfOpenCalls = 0;
            HalfOpenStartedUtc = now;
            _halfOpenGeneration++;
            _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.HalfOpen.ToString(), "half_opened");
        }

        private void Close()
        {
            var previous = Status;
            Status = CircuitStatus.Closed;
            FailureCount = 0;
            ActiveHalfOpenCalls = 0;
            SuccessfulHalfOpenCalls = 0;
            if (previous != CircuitStatus.Closed)
            {
                _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.Closed.ToString(), "closed");
            }
        }

        private void Open()
        {
            var previous = Status;
            Status = CircuitStatus.Open;
            ActiveHalfOpenCalls = 0;
            SuccessfulHalfOpenCalls = 0;
            if (previous != CircuitStatus.Open)
            {
                _metrics?.RecordCircuitEvent(_circuitKey, CircuitStatus.Open.ToString(), "opened");
            }
        }

        private sealed class CircuitBreakerExecutionLease : ICircuitBreakerExecutionLease
        {
            private readonly CircuitStateEntry _entry;
            private readonly CircuitStatus _acquiredStatus;
            private readonly int _halfOpenGeneration;
            private readonly TimeProvider _timeProvider;
            private int _completed;

            public CircuitBreakerExecutionLease(
                CircuitStateEntry entry,
                string circuitKey,
                CircuitStatus acquiredStatus,
                int halfOpenGeneration,
                TimeProvider timeProvider)
            {
                _entry = entry;
                CircuitKey = circuitKey;
                _acquiredStatus = acquiredStatus;
                _halfOpenGeneration = halfOpenGeneration;
                _timeProvider = timeProvider;
            }

            public string CircuitKey { get; }

            public void ReportSuccess()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 1)
                {
                    return;
                }

                _entry.ReportLeaseSuccess(_acquiredStatus, _halfOpenGeneration);
            }

            public void ReportFailure(CircuitFailureSeverity severity = CircuitFailureSeverity.Transient)
            {
                if (Interlocked.Exchange(ref _completed, 1) == 1)
                {
                    return;
                }

                _entry.ReportLeaseFailure(
                    _acquiredStatus,
                    _halfOpenGeneration,
                    _timeProvider.GetUtcNow(),
                    severity);
            }

            public void Release()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 1)
                {
                    return;
                }

                _entry.ReleaseLease(_acquiredStatus, _halfOpenGeneration, _timeProvider.GetUtcNow());
            }

            public void Dispose() => Release();
        }
    }
}
