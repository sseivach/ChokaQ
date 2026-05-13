# Resilience And Recovery Hardening Roadmap

This file tracks hardening work for ChokaQ's circuit breaker, zombie rescue,
and backpressure/recovery policies. It is intentionally private and not linked
from the public documentation navigation.

The audit is partly outdated. Circuit breaker integration is already in
`JobProcessor`: jobs are checked before execution and rescheduled when the
circuit is open. Zombie rescue also already uses configurable scan intervals and
SQL storage supports per-queue zombie timeouts through the queue table.

The high-priority issue is narrower: half-open circuit permits can be acquired
without a guaranteed release/report path when execution exits before
`ReportSuccess` or `ReportFailure`.

## Current State

Strong parts already implemented:

- `InMemoryCircuitBreaker` has a lock-free fast path for closed circuits.
- State mutation is protected by a small per-circuit lock.
- Open, closed, and half-open states are modeled explicitly.
- Half-open probes are limited by `HalfOpenMaxCalls`.
- Fatal failures immediately open the circuit.
- `JobProcessor` checks `IsExecutionPermitted(jobType)` before dispatch.
- Circuit breaker rejection reschedules the job instead of executing it.
- `JobProcessor` reports success and failure for normal handler outcomes.
- `ZombieRescueService` separates abandoned fetched jobs from true processing
  zombies.
- Fetched jobs return to Pending because user code has not run yet.
- Processing zombies move to DLQ instead of automatic retry.
- Zombie rescue only notifies the dashboard when state actually changed.
- Recovery scan interval is configurable through `ChokaQOptions.Recovery`.
- SQL zombie timeout uses per-queue `ZombieTimeoutSeconds` with a global
  fallback.
- Processing jobs that kill the worker process after `MarkAsProcessing` are
  later treated as zombies and moved to DLQ, not automatically returned to
  `Pending`.

Remaining risks:

- Half-open circuit permits are not represented as disposable leases.
- A half-open permit can leak if execution is skipped after permit acquisition
  or if shutdown/cancellation happens before success/failure reporting.
- Half-open success closes the circuit immediately even when multiple probes may
  still be running.
- Failure counting is threshold-based but not windowed.
- There is no ignored/neutral failure severity.
- Global throughput throttling does not exist beyond per-queue `MaxWorkers`.
- Zombie rescue scan interval is static rather than adaptive.
- There is no dedicated process-crash or crash-loop failure taxonomy. A job that
  terminates the process without a managed exception is currently surfaced as
  `Zombie`.
- There is no resurrection guard for repeated zombies that may indicate a
  process-killing payload or handler defect.
- Resurrection resets `AttemptCount`, so repeated crash/requeue cycles cannot be
  detected reliably from `AttemptCount > N` alone.
- The system has no durable per-job execution event log that can prove "started
  but produced no managed failure record" across process crashes.

## Design Principles

- Circuit breaker permits are execution leases and must always be completed or
  released.
- Half-open should never become an indefinite stuck state.
- Recovery scans should balance time-to-recovery with database load.
- Fetched recovery and processing zombie handling must remain separate.
- Processing zombies should remain DLQ-first because side effects may already
  have happened.
- Process-corrupted failures such as stack overflow or access violation are not
  catchable in normal managed error handling. Recovery must rely on persisted
  state, heartbeat expiry, worker identity, and operator review.
- Do not infer "malicious" only from missing logs. Logs can be dropped for many
  benign reasons; classification should use durable execution history if it is
  introduced.
- Backpressure should be based on queue lag, worker capacity, and dependency
  health, not queue depth alone.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until higher-risk items are complete. |

## Executive Sequence

1. Fix half-open permit lifecycle so permits cannot leak.
2. Add tests for half-open success, failure, cancellation, stale lease, and
   shutdown paths.
3. Decide whether half-open closes on first success or after all probes finish.
4. Add rolling-window or decay behavior for failure counts.
5. Add ignored/neutral failure severity if real use cases appear.
6. Document process-killing job behavior and why Processing zombies are not
   auto-retried.
7. Add adaptive zombie scan interval or document why static interval is enough.
8. Add crash-loop resurrection guard only if repeated zombie resurrection becomes
   a real operator problem.
9. Add global backpressure controls after queue-level correctness remains stable.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| `StackOverflowException` or `AccessViolationException` can terminate the worker process. | Valid. These are process-corrupted failures and cannot be reliably handled by normal `catch` blocks. | Rely on persisted state and zombie rescue; document that handlers must avoid unsafe process-killing behavior. |
| Zombie rescue will automatically re-run the same process-killing job forever. | Mostly outdated for SQL mode. `Fetched` jobs return to `Pending` only before user code starts. `Processing` zombies move to DLQ for operator review. | Preserve DLQ-first Processing zombie behavior and add regression tests/docs. |
| Use `AttemptCount > 5` plus no logged error to mark the job malicious. | Weak as stated. `AttemptCount` resets on resurrection, and absence of logs is not durable evidence. | If needed later, add a durable execution/crash history or repeated-zombie counter and classify as `CrashLoop` or `ProcessCrash`, not generic "malicious" by default. |
| Repeated manual resurrection can recreate a process crash. | Valid operational risk. It is not an automatic engine loop, but an operator can requeue the same zombie repeatedly. | Add warnings/guards for repeated `Zombie` resurrection before stable production claims. |

## Phase A: Circuit Breaker Baseline

Goal: preserve the current circuit breaker strengths.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Keep lock-free closed fast path. | `IsExecutionPermitted` returns immediately when status is Closed. |
| A.2 | P0 | Done | Keep locked state mutation. | Success, failure, and half-open state changes are synchronized. |
| A.3 | P0 | Done | Keep half-open limit. | Half-open allows at most `HalfOpenMaxCalls` probes at a time. |
| A.4 | P0 | Done | Keep fatal failure severity. | `CircuitFailureSeverity.Fatal` opens the circuit immediately. |
| A.5 | P1 | Done | Surface circuit stats. | The Deck can display Closed/Open/HalfOpen states. |

## Phase B: Half-Open Permit Lifecycle

Goal: guarantee that every half-open permit has a completion path.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Partial | Limit half-open concurrency. | Permits are counted, but not leased/released independently. |
| B.2 | P0 | Open | Add permit release API or lease object. | A permit acquired in HalfOpen can be released without closing or opening the circuit. |
| B.3 | P0 | Open | Release on skipped execution. | If `MarkAsProcessing` rejects a stale job after permit acquisition, the permit is released. |
| B.4 | P0 | Open | Release on shutdown/cancellation before report. | Worker shutdown cannot leave a circuit stuck in HalfOpen. |
| B.5 | P1 | Open | Decide multi-probe close semantics. | Circuit either closes on first success by design, or waits for all half-open probes to finish successfully. |
| B.6 | P1 | Open | Add half-open timeout safety. | A circuit stuck in HalfOpen longer than a configured timeout returns to Open or allows a new probe. |

Candidate API:

```csharp
public readonly struct CircuitPermit : IDisposable
{
    public bool IsAllowed { get; }
    public bool IsProbe { get; }
    public void ReportSuccess();
    public void ReportFailure(CircuitFailureSeverity severity);
    public void Dispose(); // releases an unreported half-open probe
}
```

Lower-impact alternative:

```csharp
public void ReleaseHalfOpenPermit(string circuitKey)
{
    // Decrement ActiveHalfOpenCalls only while still HalfOpen.
}
```

Preferred direction:

- Use a lease-style API long term so call sites cannot forget release/report.
- Keep the old `IsExecutionPermitted` API as compatibility wrapper if needed.

## Phase C: Circuit Failure Window

Goal: avoid a forever counter that does not represent recent dependency health.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Count transient failures. | Current breaker increments `FailureCount` until success resets it. |
| C.2 | P1 | Open | Add rolling failure window or decay. | Old failures stop contributing after a configured window. |
| C.3 | P1 | Open | Add policy fields. | `CircuitPolicy` can configure failure window duration. |
| C.4 | P2 | Open | Add dashboard window display. | Circuit view explains whether failures are recent-window counts or lifetime since last success. |

Simple decay option:

```csharp
if (now - LastFailureUtc > Policy.FailureWindow)
{
    FailureCount = 0;
}
```

More precise option:

- Store recent failure timestamps in a small bounded queue.
- Count only timestamps inside the active window.

## Phase D: Failure Severity Semantics

Goal: make circuit impact match the kind of failure.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P0 | Done | Support transient failures. | Transient failures contribute to threshold. |
| D.2 | P0 | Done | Support fatal failures. | Fatal failure opens the circuit immediately. |
| D.3 | P2 | Open | Add ignored or neutral severity. | Known non-dependency outcomes can avoid changing circuit state. |
| D.4 | P2 | Open | Add classification guidance. | Docs explain which exceptions should be transient, fatal, or ignored. |

Potential enum:

```csharp
public enum CircuitFailureSeverity
{
    Ignored,
    Transient,
    Fatal
}
```

## Phase E: Circuit Breaker Pipeline Integration

Goal: keep circuit breaker decisions aligned with job execution semantics.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P0 | Done | Check circuit before handler execution. | `JobProcessor` calls `IsExecutionPermitted(jobType)` before dispatch. |
| E.2 | P0 | Done | Reschedule when circuit is open. | Open circuit schedules the job after `Retry.CircuitBreakerDelay`. |
| E.3 | P1 | Partial | Use job type as circuit key. | Current key is job type; dependency-level keys may be needed for multiple handlers sharing one dependency. |
| E.4 | P1 | Open | Allow dependency-specific circuit keys. | Jobs or handlers can map to a dependency key such as `stripe-api` or `email-provider`. |
| E.5 | P1 | Open | Avoid permit acquisition before storage lease is valid. | Circuit permit lifecycle accounts for `MarkAsProcessing` rejection. |
| E.6 | P2 | Open | Add circuit metrics. | Metrics count open rejects, half-open probes, closes, opens, and permit leaks prevented. |

Audit note:

The note's suggestion to integrate the circuit breaker into the worker pipeline
is already implemented at the `JobProcessor` level. The remaining improvement is
permit lifecycle and better circuit-key modeling.

## Phase F: Zombie Rescue Baseline

Goal: preserve the correct recovery split between abandoned fetched work and
dead processing work.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P0 | Done | Recover abandoned fetched jobs. | Fetched jobs whose worker died before execution return to Pending. |
| F.2 | P0 | Done | Archive processing zombies. | Processing jobs with expired heartbeat move to DLQ. |
| F.3 | P0 | Done | Keep processing zombies out of automatic retry. | Zombie side effects may already have happened, so operator review is required. |
| F.4 | P1 | Done | Notify only on change. | Dashboard stats notification is sent only when rows changed. |
| F.5 | P1 | Done | Use configurable scan interval. | `Recovery.ScanInterval` controls scan frequency. |
| F.6 | P1 | Done | Support per-queue zombie timeout in SQL. | SQL uses queue-specific `ZombieTimeoutSeconds` with global fallback. |
| F.7 | P1 | Open | Document process-killing job behavior. | Docs explain that started jobs which kill the process become `Zombie` DLQ rows rather than automatic retries. |
| F.8 | P1 | Open | Add Processing zombie regression tests. | Tests prove expired `Processing` rows move to DLQ and are not returned to `Pending`. |
| F.9 | P2 | Open | Add repeated-zombie resurrection warning. | The Deck warns when an operator resurrects a job that previously landed in DLQ as `Zombie`. |
| F.10 | P2 | Deferred | Add crash-loop taxonomy. | Optional future failure reason such as `ProcessCrash` or `CrashLoop` is backed by durable evidence, not log absence. |
| F.11 | P2 | Deferred | Add durable execution attempt events. | A future event log can distinguish started/no-managed-failure crashes from ordinary heartbeat loss. |

Current crash-loop stance:

```text
Fetched crash before user code -> recover to Pending
Processing crash after user code started -> archive to DLQ as Zombie
Repeated manual resurrection -> operator responsibility today, future guardrail
```

Recommendation:

Do not implement a "malicious if AttemptCount > 5 and no error log" rule for the
first NuGet preview. It is not reliable with the current data model. The correct
near-term work is documentation and tests proving that Processing zombies do not
auto-retry.

## Phase G: Adaptive Recovery Scanning

Goal: reduce time-to-recovery during incidents without adding unnecessary load
when the system is quiet.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P2 | Partial | Use static configurable interval. | Current interval is configurable but fixed. |
| G.2 | P2 | Open | Add adaptive interval option. | Scanner can run faster after changes and slower when no work is recovered. |
| G.3 | P2 | Open | Add min/max scan interval settings. | Adaptive behavior is bounded and visible in configuration. |
| G.4 | P2 | Open | Add scan metrics. | Metrics expose scan duration, recovered count, archived zombie count, and next delay. |

Candidate behavior:

```csharp
var nextDelay = statsChanged
    ? options.Recovery.ActiveScanInterval
    : options.Recovery.IdleScanInterval;
```

This is useful, but not a production-preview blocker while the static interval
is configurable.

## Phase H: Global Backpressure

Goal: add system-wide pressure limits after per-queue bulkheads are stable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Done | Enforce per-queue concurrency. | `MaxWorkers` limits active jobs per queue at the storage/fetch layer. |
| H.2 | P1 | Partial | Use worker-local concurrency limiter. | SQL worker has local dynamic concurrency limiter. |
| H.3 | P2 | Open | Add global inflight limit. | Cluster or process can cap total active jobs across queues. |
| H.4 | P2 | Open | Add jobs-per-second throttle. | Optional rate limit protects dependencies or database under high load. |
| H.5 | P2 | Open | Connect backpressure to queue lag SLOs. | Operators can tune scale-up/down and throttle behavior from lag signals. |

This belongs after the high-priority execution and idempotency hardening work.

## Phase I: Tests

Goal: prove resilience behavior under edge conditions.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P0 | Open | Add half-open permit leak test. | Permit acquired then skipped/canceled does not leave circuit stuck HalfOpen. |
| I.2 | P0 | Open | Add half-open success/failure tests. | Success and failure transitions work with `HalfOpenMaxCalls > 1`. |
| I.3 | P1 | Open | Add rolling-window tests. | Old failures do not open the circuit after the window expires. |
| I.4 | P1 | Open | Add circuit integration tests. | Open circuit reschedules without handler execution; half-open reports correctly. |
| I.5 | P1 | Open | Add zombie per-queue timeout tests. | Queue-specific timeout overrides global timeout in SQL and in-memory storage. |
| I.6 | P2 | Open | Add adaptive scan tests. | Scanner picks active or idle delay according to recovery outcome. |
| I.7 | P1 | Open | Add process-crash zombie test. | A simulated expired `Processing` job with no managed failure record moves to DLQ, not `Pending`. |
| I.8 | P2 | Open | Add repeated resurrection guard test. | If a future guard is added, repeated zombie resurrection produces warning, block, or explicit override behavior. |

## Suggested Implementation Order

1. Add failing tests for half-open permit leak.
2. Add a permit release or lease-based circuit API.
3. Wire permit completion through `JobProcessor` success, failure, stale lease,
   shutdown, and cancellation paths.
4. Add tests for `HalfOpenMaxCalls > 1`.
5. Add failure window or simple decay to `CircuitPolicy`.
6. Add circuit metrics.
7. Add zombie docs/tests that pin `Processing -> DLQ`, especially for simulated
   process crash cases.
8. Add adaptive zombie scan only after high-priority breaker behavior is fixed.
9. Add crash-loop resurrection guard only after real operator demand or stable
   event history exists.
10. Add global backpressure controls in a later release.

## Non-Goals

- Do not replace the custom circuit breaker with a large resilience dependency
  unless requirements outgrow the local implementation.
- Do not automatically retry processing zombies.
- Do not classify jobs as malicious based only on missing logs.
- Do not add global rate limiting before queue-level and execution-level
  correctness is stable.
- Do not make adaptive scanning mandatory for simple installations.
