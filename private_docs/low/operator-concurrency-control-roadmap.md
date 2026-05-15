# Operator Concurrency Control Roadmap

This file tracks hardening work for operator-controlled runtime concurrency:
dashboard controls, runtime throttling semantics, audit, persistence, and
multi-node behavior. It is intentionally private and not linked from the public
documentation navigation.

Priority: low.

Reason: runtime concurrency control is an important operational pressure valve,
especially during SQL or dependency saturation, but it is not a first NuGet
preview blocker because ChokaQ already supports process-local runtime capacity
changes through `IWorkerManager.UpdateWorkerCount` and
`DynamicConcurrencyLimiter.SetCapacity`.

## Current State

Strong parts already implemented:

- `SqlJobWorker` uses `DynamicConcurrencyLimiter`.
- `SqlJobWorker.ActiveWorkers` exposes current running execution count.
- `SqlJobWorker.TotalWorkers` exposes current process-local capacity.
- `SqlJobWorker.UpdateWorkerCount(count)` changes capacity at runtime.
- Reducing capacity does not cancel running jobs; active work drains naturally.
- The Deck Settings panel already has a `MAX WORKERS` input.
- The Settings panel applies changes through the local `IWorkerManager`.
- Health checks expose active and total worker counts.

Important correction:

- ChokaQ does not currently require a service restart to change worker capacity
  for a running process. That part is already implemented.
- The current UI control is process-local, not a cluster-wide SignalR broadcast
  to every worker node.
- Capacity reduction should not cancel running job tokens by default. It should
  stop new executions from starting until active work drains below the new
  limit.

Remaining gaps:

- The control is buried in Settings instead of being a first-class operator
  control near live health signals.
- The current control is an input plus Apply button, not a fast pressure-control
  slider or stepper.
- Runtime capacity changes are not persisted across restart.
- Runtime capacity changes are not audited as operator actions.
- Runtime capacity changes are not rate-limited or guarded by a dedicated
  policy.
- Runtime capacity is process-local; multi-node deployments need an explicit
  cluster control model.
- Operators do not see projected drain behavior when lowering capacity below
  current active count.
- There is no direct link between queue lag, SQL pressure, dependency health,
  and recommended capacity changes.

## Design Principles

- Concurrency control is a pressure valve, not a destructive action.
- Lowering concurrency should drain active jobs, not kill them.
- Killing running jobs should remain a separate explicit cancellation workflow.
- Operators need immediate feedback: target capacity, active workers, queued
  work, queue lag, and saturation.
- Process-local and cluster-wide controls must be visually and semantically
  different.
- Runtime overrides should be auditable and optionally persistent.
- Control surfaces should have safe bounds and clear ownership.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until process-local control is hardened. |

## Executive Sequence

1. Preserve existing process-local runtime capacity changes.
2. Move or mirror max concurrency into a first-class operator control near live
   health signals.
3. Make downscale semantics explicit: drain active work, do not cancel running
   jobs.
4. Add validation, authorization, rate limiting, and audit for capacity changes.
5. Add optional persistence for runtime overrides.
6. Add metrics and history for capacity changes.
7. Define cluster-wide concurrency control separately from process-local control.

## Latest Audit Classification

| Finding | Current Status | Action |
|---|---|---|
| Capacity is hardcoded and requires restart. | Outdated. SQL worker capacity can change at runtime through `UpdateWorkerCount`. | Preserve runtime limiter behavior. |
| A dashboard control for max concurrency is valuable. | Valid. Current control exists in Settings but is not prominent or optimized for incidents. | Add first-class operator control. |
| SignalR should send the command to all workers. | Partially valid. Needed for multi-node cluster control, but current app controls only the local process. | Design cluster scope explicitly. |
| Reducing capacity should stop pressure without stopping processing. | Valid. Current limiter naturally drains active work and blocks new starts. | Document and expose drain semantics. |
| Reducing capacity should cancel extra running jobs. | Rejected as default behavior. Cancelling running jobs can duplicate side effects and belongs to explicit cancellation. | Keep cancellation separate. |
| Operators need fast incident UX. | Valid. A header slider/input is better than a buried Settings field during DB pressure. | Add operator-bar control with feedback. |
| Runtime override should survive restart. | Optional but useful. Current override is process memory only. | Add persisted override after audit model is defined. |

## Phase A: Preserve Process-Local Runtime Control

Goal: keep the already-working runtime capacity mechanism.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Use dynamic limiter in SQL worker. | SQL worker gates execution with `DynamicConcurrencyLimiter`. |
| A.2 | P0 | Done | Expose active count. | `IWorkerManager.ActiveWorkers` returns current running jobs. |
| A.3 | P0 | Done | Expose target capacity. | `IWorkerManager.TotalWorkers` returns limiter capacity. |
| A.4 | P0 | Done | Change capacity at runtime. | `IWorkerManager.UpdateWorkerCount` calls `SetCapacity` without restart. |
| A.5 | P1 | Done | Add runtime capacity regression tests. | Scaling down below active count blocks new jobs until active work drains. |
| A.6 | P1 | Done | Document drain semantics. | Docs state that capacity reduction does not cancel running jobs. |

## Phase B: Operator Bar UX

Goal: make concurrency control usable during incidents.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P1 | Partial | Expose max workers in The Deck. | Current Settings panel has `MAX WORKERS`, but it is not first-viewport incident UX. |
| B.2 | P1 | Open | Add operator-bar control. | Dashboard header or health band shows target concurrency, active count, and edit control. |
| B.3 | P1 | Open | Add slider/stepper/input combo. | Operators can adjust capacity quickly while still entering exact values. |
| B.4 | P1 | Open | Show drain state. | If active count is above target, UI shows `draining` until active count reaches target. |
| B.5 | P1 | Open | Show pressure context. | Control appears near queue lag, active workers, SQL health, and failure rate. |
| B.6 | P2 | Open | Add recommended capacity hint. | UI can suggest lower capacity during SQL pressure or dependency throttling. |

UX rule:

- Use a slider for fast pressure control.
- Keep an input or stepper for precise values.
- Do not hide active worker count or target capacity while the operator is
  changing the value.

## Phase C: Safety Guards And Authorization

Goal: make capacity changes safe without making them cumbersome.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Clamp capacity bounds. | Current Settings logic clamps 0-100; backend boundary should enforce policy too. |
| C.2 | P1 | Open | Add server-side validation. | Hub or API rejects negative, excessive, malformed, or unauthorized capacity changes. |
| C.3 | P1 | Open | Add dedicated authorization policy. | Runtime scaling can require operator permission distinct from read-only dashboard access. |
| C.4 | P1 | Open | Add rate limiting. | Repeated capacity changes by one actor/session are throttled. |
| C.5 | P2 | Open | Add warning for zero capacity. | Setting capacity to zero or near-zero requires explicit intent if supported. |

Recommended default:

- Minimum 1 for normal production control.
- Allow 0 only if explicitly treated as "pause all new execution" and clearly
  separated from queue pause/cancel workflows.

## Phase D: Audit And Change History

Goal: make runtime pressure changes reconstructable after incidents.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Open | Add capacity-change audit event. | Actor, old capacity, new capacity, scope, timestamp, and outcome are recorded. |
| D.2 | P1 | Open | Add operation ID. | Logs, audit event, metrics, and UI message share one operation ID. |
| D.3 | P2 | Open | Add recent changes view. | Operators can see who changed capacity recently and why. |
| D.4 | P2 | Open | Add optional reason field. | Incident operators can record context such as `SQL CPU saturation`. |
| D.5 | P2 | Open | Add audit tests. | Capacity changes record actor, bounds, old/new values, and failed attempts. |

Candidate audit shape:

```csharp
public sealed record WorkerCapacityChangedAudit(
    string OperationId,
    string Actor,
    string Scope,
    int OldCapacity,
    int NewCapacity,
    DateTimeOffset ChangedAtUtc,
    string Outcome,
    string? Reason);
```

## Phase E: Persistence And Runtime Overrides

Goal: decide whether runtime capacity should survive restart.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Partial | Support in-memory override. | Current runtime update changes the process-local limiter only. |
| E.2 | P2 | Open | Add optional persisted override. | Hosts can persist runtime capacity in storage or configuration-backed state. |
| E.3 | P2 | Open | Define startup precedence. | Docs state whether config, persisted override, or default value wins at startup. |
| E.4 | P2 | Open | Add override expiry. | Operators can set temporary overrides that revert after a duration. |
| E.5 | P2 | Open | Add reset-to-config action. | UI can clear runtime override and return to configured baseline. |

Recommended policy:

- Start with process-local non-persistent override.
- Add persistence only with audit and reset semantics.
- Temporary overrides are safer than silent permanent drift from config.

## Phase F: Cluster-Wide Control

Goal: avoid pretending a local process control is a distributed control plane.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Partial | Support process-local control. | Current `IWorkerManager` controls one process instance. |
| F.2 | P2 | Open | Define control scope in UI. | UI labels capacity as local process, node, queue, or cluster. |
| F.3 | P2 | Open | Add worker identity model. | Each worker process reports node ID, heartbeat, active count, and target capacity. |
| F.4 | P2 | Open | Add per-node capacity command. | Operators can change capacity for a specific node. |
| F.5 | P2 | Deferred | Add cluster-wide capacity command. | A command can target all nodes only after worker identity and audit are reliable. |
| F.6 | P2 | Deferred | Add distributed desired-state store. | Workers reconcile desired capacity from storage instead of relying only on SignalR command delivery. |

Important distinction:

- SignalR is fine for UI command transport.
- Cluster-wide worker control should not depend only on best-effort browser
  command delivery. Workers need a durable desired state or a reliable control
  channel if this becomes production-critical.

## Phase G: Metrics And Feedback

Goal: show whether concurrency changes actually reduced pressure.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Expose active and total workers. | Health checks already include active and total worker counts. |
| G.2 | P1 | Open | Add capacity-change metric. | Metrics record old capacity, new capacity, scope, and actor category where safe. |
| G.3 | P1 | Open | Add limiter saturation metric. | Operators can see time spent at full capacity. |
| G.4 | P1 | Open | Add concurrency wait metric. | Jobs waiting for execution slots expose wait duration. |
| G.5 | P2 | Open | Correlate with queue lag and SQL health. | Dashboard can show whether downscaling helped SQL pressure or increased lag. |

## Phase H: Tests

Goal: prove runtime control behavior before making it more prominent.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P1 | Done | Add downscale drain test. | Reducing capacity below active count does not cancel running jobs and prevents new starts until drained. |
| H.2 | P1 | Done | Add upscale wakeup test. | Increasing capacity allows additional waiting jobs to start. |
| H.3 | P1 | Open | Add UI validation tests. | Invalid values are rejected or clamped consistently in UI and backend. |
| H.4 | P1 | Open | Add authorization tests. | Read-only users cannot change runtime capacity. |
| H.5 | P1 | Open | Add audit tests. | Successful and rejected changes produce expected audit records. |
| H.6 | P2 | Open | Add multi-node scope tests. | Commands affect only the intended local node, selected node, or cluster scope. |

## Suggested Implementation Order

1. Add tests for current process-local `UpdateWorkerCount` downscale/upscale
   semantics. Done for SQL worker runtime downscale and limiter scale-up.
2. Add backend validation and authorization boundary for capacity changes.
3. Add audit event for runtime capacity changes.
4. Move or mirror the control into a dashboard operator bar.
5. Add drain-state UI and pressure context.
6. Add optional persistence and reset-to-config semantics.
7. Design cluster-wide control only after process-local control is audited and
   clearly labeled.

## Non-Goals

- Do not cancel running jobs when lowering concurrency by default.
- Do not market process-local capacity as cluster-wide control.
- Do not add autoscaling in this roadmap; adaptive scaling is tracked in
  `low/self-tuning-concurrency-roadmap.md`.
- Do not bypass authorization and audit just because the control is convenient.
- Do not make SignalR notification delivery part of worker correctness.
