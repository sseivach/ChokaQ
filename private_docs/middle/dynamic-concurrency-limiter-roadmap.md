# Dynamic Concurrency Limiter Hardening Roadmap

This file tracks hardening work for `DynamicConcurrencyLimiter`, the custom
elastic concurrency primitive used by the SQL worker. It is intentionally
private and not linked from the public documentation navigation.

Priority: middle.

Reason: the limiter is in the production SQL worker path, so correctness matters.
The current design is strong and state-driven; the remaining risks are rare
liveness, misuse-detection, fairness, and shutdown edge cases rather than known
job-state corruption bugs.

## Current State

Strong parts already implemented:

- Concurrency is based on state, not physical permits.
- Fast path uses `Interlocked.CompareExchange`.
- Waiters use async suspension rather than blocking threads.
- Wakeups are coalesced through a bounded channel with capacity 1.
- Successful acquisition relays one wakeup when more capacity remains.
- Capacity can be changed at runtime.
- Running count and capacity are observable.
- Unit tests cover basic acquire/release behavior, scale-up, scale-down,
  disposal, and high-concurrency capacity enforcement.

Important correction:

- The current scale-down behavior does not burn permits. It lowers
  `_targetCapacity` and lets active workers drain naturally. That is the right
  state-driven model, but some test names still use older "burn permit"
  language.

Remaining risks:

- `Release` masks double-release bugs by resetting `_activeWorkers` to zero.
- There is no lease-style API that pairs acquire and release safely.
- Wait liveness under rapid release, resize, cancellation, and dispose races
  needs stress coverage.
- Dispose behavior is mostly safe, but post-wakeup and release-after-dispose
  policy is not explicit.
- Channel completion during disposal does not carry a disposal exception reason,
  so waiters only infer disposal from the closed channel path.
- Fairness is intentionally not guaranteed; this should be documented as a
  throughput tradeoff.
- Large scale-up relies on relay wakeup. That is usually correct, but it needs a
  many-waiter regression test.
- A naive burst loop over `_signal.Writer.TryWrite(1)` would not create many
  queued wakeups while the signal channel capacity is 1.
- Micro-optimizations such as rewriting the fast-path branch should be deferred
  until benchmarks show the limiter itself is material to end-to-end latency.

## Design Principles

- The limiter controls throughput, not FIFO fairness.
- Signals are hints that state may have changed; state is the source of truth.
- One coalesced wakeup is enough only if relay behavior is correct and tested.
- Misuse such as double release should be visible, not silently repaired.
- Shutdown must never leave waiters suspended indefinitely.
- Any fairness feature should be opt-in because it will add overhead.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until real requirements justify it. |

## Executive Sequence

1. Preserve the state-driven limiter model.
2. Add tests for double release, dispose races, resize races, and many-waiter
   scale-up.
3. Decide whether double release throws by default or only in strict/debug mode.
4. Consider an acquire lease API to make correct usage easier.
5. Document no-FIFO fairness as an intentional throughput tradeoff.
6. Add limiter metrics before changing wakeup mechanics.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| Lock-free fast path and CAS model are strong. | Valid strength. | Preserve. |
| Coalesced channel signal is a good wakeup primitive. | Valid strength. | Preserve unless tests show liveness problems. |
| Relay wakeup prevents stampedes and supports smooth scale-up. | Valid strength. | Add many-waiter scale-up tests. |
| Pre-check before `ReadAsync` is required to avoid lost wakeups. | Partially valid. `ReadAsync` already consumes an existing signal, and state is rechecked after wakeup. The real need is liveness coverage under race-heavy workloads. | Add stress tests first; only change wait loop if tests expose latency or missed-wakeup behavior. |
| Starvation is possible under competitive scheduling. | Valid by design. | Document no-FIFO fairness; defer fair queue unless needed. |
| Dispose race needs explicit handling. | Partially valid. Waiters check `_disposed` and channel close maps to `ObjectDisposedException`; post-wakeup and release-after-dispose behavior need tests. | Add dispose race tests and document policy. |
| Dispose should complete the signal with an exception. | Optional cleanup. Current behavior maps closed channel to `ObjectDisposedException`, but `TryComplete(exception)` can make the shutdown cause explicit. | Consider after dispose-race tests define desired behavior. |
| Double release is silently masked. | Valid. Current code resets running count to zero. | Prefer fail-fast or strict-mode detection after auditing call sites. |
| `SetCapacity` may not wake enough waiters. | Partially valid. Relay should propagate scale-up, but this needs many-waiter tests. A burst loop is ineffective with channel capacity 1 unless the signal design changes. | Test relay scale-up; consider multi-signal design only if needed. |
| Fairness should be added. | Deferred. Background job processing values throughput over FIFO waiter fairness. | Revisit only for user-facing or latency-critical throttling. |
| Fast path can be micro-optimized with different branching. | Low priority. Branch layout changes are not meaningful without benchmark evidence. | Measure before changing readability or control flow. |

## Phase A: Preserve Baseline

Goal: keep the parts that make the limiter useful for SQL worker throughput.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Use state-driven concurrency. | `RunningCount < Capacity` is the acquisition condition. |
| A.2 | P0 | Done | Use CAS fast path. | Available capacity is acquired through `Interlocked.CompareExchange`. |
| A.3 | P0 | Done | Use async waiters. | No blocking thread wait is required while capacity is exhausted. |
| A.4 | P0 | Done | Use coalesced wakeups. | Signal channel has capacity 1 and does not accumulate unbounded notifications. |
| A.5 | P1 | Done | Use relay wakeup. | A successful acquisition signals another waiter when capacity remains. |
| A.6 | P1 | Done | Support dynamic capacity. | `SetCapacity` changes future acquisition limits without replacing the limiter. |

## Phase B: Misuse Detection

Goal: make incorrect acquire/release pairing visible.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P1 | Partial | Detect negative running count. | Current code detects it but resets to zero silently. |
| B.2 | P1 | Open | Decide release misuse policy. | Double release either throws, logs a structured error, or increments a misuse metric by explicit design. |
| B.3 | P1 | Open | Add double-release test. | Calling `Release` more times than successful `WaitAsync` is visible and cannot hide accounting bugs. |
| B.4 | P2 | Open | Add strict mode if needed. | Production can choose fail-fast or best-effort behavior through an explicit option. |

Recommended direction:

- Prefer throwing after current call sites are audited.
- If throwing inside worker cleanup is considered too risky, expose a strict
  mode and always emit telemetry.

## Phase C: Lease-Based API

Goal: reduce accidental double release or missed release by making the API
structured.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P2 | Open | Add `AcquireAsync` lease API. | Callers can use `using`/`await using` to release exactly once. |
| C.2 | P2 | Open | Make lease release idempotent. | Disposing the same lease twice does not decrement running count twice. |
| C.3 | P2 | Open | Keep `WaitAsync` compatibility. | Existing call sites continue to work while new code can use leases. |
| C.4 | P2 | Open | Migrate SQL worker to lease API. | SQL worker processing tasks release through the lease in `finally`. |

Candidate shape:

```csharp
public readonly struct ConcurrencyLease : IDisposable
{
    public void Dispose();
}

public async ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken ct);
```

## Phase D: Wakeup And Resize Liveness

Goal: prove waiters do not sleep indefinitely when capacity exists.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Partial | Wake one waiter on release. | Current `Release` writes one coalesced signal when capacity allows. |
| D.2 | P1 | Partial | Wake chain on scale-up. | Current `SetCapacity` writes one signal and relies on relay to propagate. |
| D.3 | P1 | Open | Add many-waiter scale-up test. | Scaling from low to high capacity wakes enough waiters without waiting for unrelated releases. |
| D.4 | P1 | Open | Add release/resize race stress test. | Rapid `Release`, `SetCapacity`, cancellation, and acquisition never leave capacity idle with stuck waiters. |
| D.5 | P2 | Open | Evaluate wait-loop pre-drain. | Add `TryRead`/pre-check only if tests show measurable missed-wakeup latency. |
| D.6 | P2 | Deferred | Change signal channel capacity. | Only if the coalesced signal plus relay model fails liveness tests. |

Important note:

With channel capacity 1, this is ineffective as a burst strategy:

```csharp
for (var i = 0; i < newCapacity; i++)
{
    _signal.Writer.TryWrite(1);
}
```

Only one wakeup can be retained. Scale-up correctness should come from relay
behavior or a deliberately different signal design.

## Phase E: Dispose And Shutdown Semantics

Goal: make shutdown behavior deterministic for waiters and active holders.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Partial | Dispose wakes waiters. | Current `Dispose` writes a signal and completes the channel. |
| E.2 | P1 | Partial | Waiting after dispose fails. | `WaitAsync` checks `_disposed` and maps closed channel to `ObjectDisposedException`. |
| E.3 | P1 | Open | Add post-wakeup disposed test. | A waiter woken by dispose cannot acquire after disposal. |
| E.4 | P1 | Open | Define release-after-dispose policy. | Releasing an already-acquired slot during shutdown is either allowed or rejected by design. |
| E.5 | P2 | Open | Consider completing signal with disposal exception. | `TryComplete` carries a disposal reason if that improves diagnostics without changing public semantics. |
| E.6 | P2 | Open | Add concurrent dispose stress test. | Dispose racing with wait, release, and resize never hangs. |

Recommended policy:

- `WaitAsync` after dispose should fail.
- `Release` after dispose may be allowed for already-acquired holders during
  shutdown, but double release must still be detected.

## Phase F: Fairness And Starvation

Goal: make the no-fairness tradeoff explicit.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Done | Use competitive scheduling. | Current implementation optimizes throughput and low overhead. |
| F.2 | P1 | Open | Document no FIFO guarantee. | Internal docs state the limiter is not appropriate for user-facing fairness-sensitive throttling. |
| F.3 | P2 | Open | Add starvation soak test. | Under high contention, tasks complete within an acceptable bound for background worker use. |
| F.4 | P3 | Deferred | Add fair limiter variant. | Only build if a real workload needs FIFO or ticket-based scheduling. |

## Phase G: Observability

Goal: expose limiter behavior when worker throughput is constrained.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Expose running count and capacity. | `RunningCount` and `Capacity` are available to worker health checks. |
| G.2 | P2 | Open | Add wait duration metric. | Operators can see how long jobs wait for a concurrency slot. |
| G.3 | P2 | Open | Add resize metric. | Capacity changes are counted with old and new capacity. |
| G.4 | P2 | Open | Add misuse metric. | Double release or invalid release attempts are visible. |
| G.5 | P2 | Open | Add saturation metric. | Time spent at full capacity is observable. |

## Phase H: Tests

Goal: prove limiter behavior under edge cases, not only simple acquire/release.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Open | Add double-release test. | Accounting misuse is visible and does not silently reset state. |
| H.2 | P1 | Open | Add many-waiter scale-up test. | Large capacity increase wakes a matching number of waiters through relay. |
| H.3 | P1 | Open | Add resize-down while active test. | Active count can temporarily exceed new capacity, but new acquisitions wait until it drains. |
| H.4 | P1 | Open | Add dispose-while-waiting test. | Waiters receive `ObjectDisposedException` and no waiter hangs. |
| H.5 | P1 | Open | Add dispose-while-active test. | Active holders can finish shutdown according to the documented release policy. |
| H.6 | P2 | Open | Add release/resize/cancel stress test. | Race-heavy workloads do not exceed capacity or leave stuck waiters. |
| H.7 | P2 | Open | Add starvation soak test. | Competitive scheduling is acceptable for background worker workloads. |

## Suggested Implementation Order

1. Add double-release, dispose race, resize race, and many-waiter tests.
2. Decide and implement double-release behavior.
3. Document no-FIFO fairness and intended use cases.
4. Add limiter wait duration and saturation metrics.
5. Consider lease-based API and migrate SQL worker if it reduces call-site risk.
6. Only change wakeup mechanics if liveness tests prove the relay model is not
   enough.

## Non-Goals

- Do not replace the limiter with `SemaphoreSlim` without a measured reason.
- Do not add FIFO fairness to the throughput limiter by default.
- Do not use this primitive for API/user-facing rate limiting without a
  fairness review.
- Do not change coalesced signaling just because a burst wakeup looks simpler;
  test liveness first.
