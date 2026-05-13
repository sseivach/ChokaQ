# Self-Tuning Concurrency Roadmap

This file tracks the future adaptive concurrency controller for ChokaQ workers.
It is intentionally private and not linked from the public documentation
navigation.

Priority: low for the first NuGet preview.

Reason: self-tuning workers can become a strong practical differentiator, but
manual runtime controls, queue lag metrics, and per-queue bulkheads are enough
for the first NuGet preview. Automatic control loops should wait until real
usage gives better signals and failure modes.

## Current State

Existing foundation:

- `DynamicConcurrencyLimiter` can change process-local capacity at runtime.
- Queue lag is recorded as the primary saturation signal.
- Active and total worker counts are exposed through health checks.
- Failure taxonomy separates throttled, fatal, timeout, transient, and zombie
  outcomes.
- SQL queues support per-queue `MaxWorkers`.
- Circuit breaker rejection can reduce pressure on unhealthy dependencies.
- Operator-controlled concurrency is tracked separately in
  `low/operator-concurrency-control-roadmap.md`.

Current gaps:

- There is no automatic worker capacity controller.
- There is no policy model for scale-up/scale-down decisions.
- There is no dry-run mode to observe what the controller would have done.
- The system does not yet correlate queue lag with SQL latency, dependency
  throttling, failure rate, CPU, or circuit state.
- There are no guardrails for min/max capacity, cooldown, or oscillation.
- There is no cluster-wide desired-state model.
- There is no way to prioritize manual operator override over automation.

## Design Principles

- Manual operator control comes before automatic control.
- A controller must be conservative by default.
- Scale-up should respond to lag only when failure and storage pressure signals
  say extra workers are safe.
- Scale-down should respond to dependency throttling, SQL pressure, high failure
  rate, or low utilization.
- Avoid oscillation with cooldowns, hysteresis, and bounded step changes.
- Start with dry-run recommendations before changing worker capacity.
- Operator overrides must win over automation.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until manual controls and metrics are stable. |

## Executive Sequence

1. Finish process-local operator concurrency controls.
2. Define controller input signals.
3. Add dry-run recommendations without changing capacity.
4. Add safe bounded controller for process-local capacity.
5. Add per-queue policy only after process-level behavior is proven.
6. Add cluster-wide desired state only after worker identity is durable.

## Phase A: Inputs And Signals

Goal: define what the controller is allowed to observe.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P1 | Done | Record queue lag. | Queue lag exists as the primary saturation signal. |
| A.2 | P1 | Partial | Expose worker utilization. | Active and total workers are available, but saturation duration needs metrics. |
| A.3 | P2 | Open | Add SQL pressure signal. | Controller can observe SQL latency, timeout rate, or storage health. |
| A.4 | P2 | Open | Add dependency pressure signal. | Throttled failures and circuit states can reduce target capacity. |
| A.5 | P2 | Open | Add failure-rate signal. | Controller avoids scaling up into a fatal payload or code bug. |
| A.6 | P2 | Open | Add queue-specific signal model. | Per-queue lag, failure rate, and `MaxWorkers` can feed later queue-level tuning. |

## Phase B: Policy Model

Goal: make tuning behavior explicit and testable.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P2 | Open | Add adaptive policy options. | Hosts can configure min, max, step size, cooldown, lag target, and pressure thresholds. |
| B.2 | P2 | Open | Add hysteresis. | Controller does not oscillate around a single threshold. |
| B.3 | P2 | Open | Add cooldown. | Capacity changes wait a minimum interval before the next change. |
| B.4 | P2 | Open | Add bounded step changes. | Controller changes capacity gradually instead of jumping from min to max. |
| B.5 | P2 | Open | Add disable switch. | Adaptive mode can be disabled completely. |

Candidate options:

```csharp
public sealed class AdaptiveConcurrencyOptions
{
    public bool Enabled { get; set; }
    public int MinCapacity { get; set; } = 1;
    public int MaxCapacity { get; set; } = 32;
    public int MaxStep { get; set; } = 2;
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan TargetQueueLag { get; set; } = TimeSpan.FromSeconds(5);
}
```

## Phase C: Dry-Run Recommendations

Goal: learn controller behavior before allowing automatic changes.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P2 | Open | Add recommendation engine. | System can calculate `increase`, `decrease`, or `hold` without applying it. |
| C.2 | P2 | Open | Add recommendation logs. | Logs explain the signal and policy that produced each recommendation. |
| C.3 | P2 | Open | Show recommendation in The Deck. | Operators can compare current capacity with suggested capacity. |
| C.4 | P2 | Open | Add accept recommendation action. | Operator can apply a recommendation manually before full automation exists. |
| C.5 | P2 | Open | Add dry-run tests. | Signal combinations produce predictable recommendations. |

## Phase D: Process-Local Controller

Goal: automate capacity changes for one process safely.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P2 | Deferred | Add controller background service. | Periodically evaluates signals and policy. |
| D.2 | P2 | Deferred | Apply capacity changes through `IWorkerManager`. | Controller uses the same boundary as operator controls. |
| D.3 | P2 | Deferred | Respect operator override. | Manual override pauses or bounds automation. |
| D.4 | P2 | Deferred | Add audit event. | Automatic capacity changes record old value, new value, policy, and reason. |
| D.5 | P2 | Deferred | Add rollback to safe value. | Controller can stop and return to configured baseline. |

## Phase E: Safety Conditions

Goal: avoid scaling into known-bad conditions.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P2 | Open | Block scale-up on SQL pressure. | High SQL latency or timeout rate prevents adding worker pressure. |
| E.2 | P2 | Open | Block scale-up on dependency throttling. | High `Throttled` rate or open circuit reduces capacity or holds. |
| E.3 | P2 | Open | Block scale-up on fatal spike. | Fatal failures do not trigger more workers just because lag rises. |
| E.4 | P2 | Open | Downscale under sustained pressure. | Controller can reduce capacity when storage or dependencies are overloaded. |
| E.5 | P2 | Open | Add emergency floor. | Controller never drops below configured minimum unless operator pauses execution. |

## Phase F: Cluster And Queue Scope

Goal: avoid accidental distributed control bugs.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P2 | Deferred | Add worker identity. | Each process reports ID, heartbeat, capacity, and active count. |
| F.2 | P2 | Deferred | Add cluster desired state. | Workers reconcile target capacity from durable storage. |
| F.3 | P2 | Deferred | Add per-queue adaptive policy. | Queue-specific lag and failure signals can tune queue limits separately from process capacity. |
| F.4 | P2 | Deferred | Add cluster safety tests. | Multiple workers do not all scale up into the same database bottleneck at once. |

## Phase G: Tests And Simulation

Goal: prove the control loop before enabling it.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P2 | Open | Add deterministic controller tests. | Given synthetic signals, controller decisions are stable and explainable. |
| G.2 | P2 | Open | Add oscillation tests. | Capacity does not flap under near-threshold signals. |
| G.3 | P2 | Open | Add pressure tests. | SQL pressure and throttling signals prevent unsafe scale-up. |
| G.4 | P2 | Open | Add operator override tests. | Manual controls take precedence over automation. |
| G.5 | P2 | Deferred | Add load simulation. | Local scenario demonstrates lag rise, scale-up, pressure downscale, and recovery. |

## Suggested Implementation Order

1. Finish operator-controlled concurrency UX and audit.
2. Add missing pressure and saturation metrics.
3. Build dry-run recommendation engine.
4. Show recommendations in The Deck.
5. Add process-local controller behind disabled-by-default options.
6. Add safety conditions and audit.
7. Consider cluster-wide desired state only after real usage demands it.

## Non-Goals

- Do not block the first NuGet preview on self-tuning concurrency.
- Do not call this AI unless there is an actual learned model.
- Do not scale up purely on queue depth.
- Do not let automation override explicit operator limits.
- Do not build cluster-wide autoscaling before worker identity and desired state
  are durable.
