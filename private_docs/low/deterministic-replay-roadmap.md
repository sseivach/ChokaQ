# Deterministic Replay Roadmap

This file tracks the future deterministic replay and execution trace feature
for ChokaQ jobs. It is intentionally private and not linked from the public
documentation navigation.

Priority: low for the first NuGet preview.

Reason: deterministic replay can become a strong differentiator after the
package has real users, but it is not required to publish a credible first
NuGet preview. It is a new product capability, not a bug fix.

## Positioning

The practical first version should not promise "re-execute production exactly."
Arbitrary user handlers can call external services, read clocks, generate
random values, and perform side effects that ChokaQ cannot deterministically
replay without cooperation.

The safe product framing is:

1. Execution trace: show what happened.
2. Replay viewer: step through the recorded execution timeline.
3. Simulation: show what retry/circuit decisions would have been under a
   different policy.
4. Deterministic handler replay: optional advanced mode only for handlers that
   opt into deterministic dependencies.

## Current State

Existing strengths that make replay plausible later:

- `JobProcessor` is the central execution orchestration point.
- `IJobStateManager` owns state transitions.
- Bus and Pipe dispatchers already have explicit pipeline boundaries.
- Middleware creates natural trace stages.
- Failure taxonomy already classifies outcomes.
- SQL storage already has Hot, Archive, and DLQ history.
- `TimeProvider` exists in the core path for some timing-sensitive behavior.
- The Deck has a job inspector surface where a trace viewer could live.

Current gaps:

- There is no execution trace event model.
- Middleware, circuit breaker, retry, heartbeat, state transitions, and
  dispatcher stages do not emit structured per-job trace events.
- There is no trace storage table or retention policy.
- There is no redaction policy for payloads, headers, exception details, or
  idempotency keys.
- There is no replay API.
- There is no deterministic dependency boundary for handler time, randomness,
  HTTP calls, database calls, or external side effects.
- There is no what-if simulation engine for retry/circuit decisions.

## Design Principles

- Never re-run external side effects by default.
- A replay viewer can be deterministic even when handler execution is not.
- Trace events must be structured and bounded, not raw log spam.
- Payload capture must respect privacy, security, and retention constraints.
- Replay should help debugging and support, not change production job state.
- What-if replay should simulate policy decisions, not mutate jobs.
- Deterministic handler replay requires explicit handler cooperation.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until trace foundations are proven. |

## Executive Sequence

1. Define a structured execution trace event model.
2. Add no-op tracer abstraction and cheap disabled path.
3. Emit trace events from `JobProcessor`, dispatchers, middleware, retry,
   circuit breaker, and state transitions.
4. Add trace storage with retention and redaction.
5. Add read-only trace API and The Deck timeline view.
6. Add replay viewer that steps through recorded trace events.
7. Add policy simulation for retry/circuit decisions.
8. Add deterministic handler replay only for opt-in handlers.

## Phase A: Trace Event Model

Goal: define the data contract before writing trace storage.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P2 | Open | Add `ExecutionTraceEvent`. | Event includes job ID, step index, stage, timestamp, severity, and structured data. |
| A.2 | P2 | Open | Add stable stage names. | Stages cover enqueue, fetch, mark processing, middleware, circuit check, dispatch, retry, DLQ, archive, and cancel. |
| A.3 | P2 | Open | Add event version. | Trace schema can evolve without breaking older stored traces. |
| A.4 | P2 | Open | Add correlation fields. | Trace can include queue, type key, attempt, worker ID, circuit key, and operation ID. |
| A.5 | P2 | Open | Add max event size. | Oversized event data is truncated or moved to a bounded blob strategy. |

Candidate shape:

```csharp
public sealed record ExecutionTraceEvent(
    string JobId,
    long StepIndex,
    DateTimeOffset TimestampUtc,
    string Stage,
    string Severity,
    string DataJson,
    int Version = 1);
```

## Phase B: Tracer Abstraction

Goal: make tracing cheap when disabled and consistent when enabled.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P2 | Open | Add `IExecutionTracer`. | Core execution can record structured events without knowing storage details. |
| B.2 | P2 | Open | Add no-op tracer. | Default preview behavior has near-zero overhead when tracing is disabled. |
| B.3 | P2 | Open | Add trace sampling or per-job enablement. | Hosts can trace failed jobs, selected queues, or specific job IDs without tracing everything. |
| B.4 | P2 | Open | Add tracer failure isolation. | Trace write failures never fail job execution. |

Candidate interface:

```csharp
public interface IExecutionTracer
{
    ValueTask RecordAsync(
        ExecutionTraceEvent traceEvent,
        CancellationToken ct = default);
}
```

## Phase C: Instrumentation Points

Goal: capture the execution path users need for debugging.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P2 | Open | Trace processor start and end. | Trace includes job ID, queue, type key, attempt, and execution duration. |
| C.2 | P2 | Open | Trace circuit breaker decision. | Trace records closed/open/half-open decision and circuit key. |
| C.3 | P2 | Open | Trace middleware stages. | Middleware can record enter/exit/short-circuit outcomes. |
| C.4 | P2 | Open | Trace dispatch and handler outcome. | Trace records handler start, success, exception type, and duration. |
| C.5 | P2 | Open | Trace retry decision. | Trace records fatal/transient/throttled classification and scheduled delay. |
| C.6 | P2 | Open | Trace state transitions. | Archive, DLQ, retry, cancel, zombie, and stale-ownership outcomes are visible. |
| C.7 | P2 | Open | Trace heartbeat degradation. | Repeated heartbeat failures can be debugged from trace context. |

## Phase D: Trace Storage

Goal: persist traces without turning storage into a new bottleneck.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P2 | Open | Add SQL trace table. | Trace events can be stored by job ID and ordered by step index. |
| D.2 | P2 | Open | Add bounded retention. | Trace data expires according to host policy. |
| D.3 | P2 | Open | Add batch writes. | Trace writes can be buffered or batched to reduce storage overhead. |
| D.4 | P2 | Open | Add failed-job trace policy. | Hosts can keep traces for DLQ jobs longer than successful jobs. |
| D.5 | P2 | Open | Add trace storage metrics. | Operators can see trace write failures, queue depth, and storage volume. |

Possible SQL shape:

```sql
CREATE TABLE ChokaQ.JobExecutionTrace (
    JobId nvarchar(64) NOT NULL,
    StepIndex bigint NOT NULL,
    TimestampUtc datetime2(7) NOT NULL,
    Stage nvarchar(128) NOT NULL,
    Severity nvarchar(32) NOT NULL,
    DataJson nvarchar(max) NULL,
    Version int NOT NULL,
    CONSTRAINT PK_JobExecutionTrace PRIMARY KEY (JobId, StepIndex)
);
```

## Phase E: Redaction And Privacy

Goal: avoid turning replay into a sensitive-data leak.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P2 | Open | Add payload capture policy. | Hosts can disable payload capture, capture metadata only, or capture redacted payload. |
| E.2 | P2 | Open | Add redaction hooks. | Hosts can redact fields before trace events are persisted. |
| E.3 | P2 | Open | Add safe defaults. | Secrets, headers, tokens, and large payloads are not captured accidentally. |
| E.4 | P2 | Open | Add retention docs. | Docs explain trace storage sensitivity and cleanup. |
| E.5 | P2 | Open | Add redaction tests. | Sensitive field examples are removed from persisted trace events. |

## Phase F: Replay Viewer

Goal: make recorded execution easy to inspect.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P2 | Open | Add trace read API. | API returns ordered trace events for a job ID. |
| F.2 | P2 | Open | Add The Deck trace timeline. | Inspector can show step-by-step execution events for Archive and DLQ jobs. |
| F.3 | P2 | Open | Add filtering by stage/severity. | Operators can jump to exception, retry, circuit, and state-transition events. |
| F.4 | P2 | Open | Add timeline export. | Support users can attach trace JSON to bug reports. |
| F.5 | P3 | Deferred | Add animated playback. | Only after the static timeline proves useful. |

## Phase G: What-If Simulation

Goal: simulate policy decisions without mutating production jobs.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P3 | Deferred | Extract retry decision model. | Retry decisions can be evaluated without dispatching a handler. |
| G.2 | P3 | Deferred | Simulate retry settings. | User can see how different `MaxAttempts`, base delay, max delay, or jitter would affect scheduling. |
| G.3 | P3 | Deferred | Simulate circuit policy. | User can see whether a circuit would reject or allow execution under alternative policy. |
| G.4 | P3 | Deferred | Show policy diff. | Replay explains which decisions change under what-if settings. |

## Phase H: Deterministic Handler Replay

Goal: support true deterministic replay only for opt-in handlers.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P3 | Deferred | Define deterministic handler contract. | Handlers opt into replay-safe dependencies and side-effect suppression. |
| H.2 | P3 | Deferred | Add replay-safe clock/random services. | Handler time and randomness can be fixed from trace context. |
| H.3 | P3 | Deferred | Add side-effect boundary. | Replay mode cannot send emails, charge cards, or call external systems unless explicitly mocked. |
| H.4 | P3 | Deferred | Add replay sandbox. | Replay runs outside normal job lifecycle and cannot archive, retry, or DLQ the original job. |
| H.5 | P3 | Deferred | Add deterministic replay tests. | Opt-in sample handler replays the same internal decisions from recorded inputs. |

## Suggested Implementation Order

1. Add trace event model and no-op tracer.
2. Instrument `JobProcessor` and dispatch boundaries behind a disabled-by-default
   tracer.
3. Add redaction policy before persisting payload-related trace data.
4. Add SQL trace storage and retention.
5. Add read-only trace timeline in The Deck.
6. Add what-if policy simulation.
7. Add deterministic handler replay only for opt-in sample handlers.

## Non-Goals

- Do not block the first NuGet preview on deterministic replay.
- Do not claim exact replay of arbitrary user side effects.
- Do not re-run production jobs by default.
- Do not store sensitive payload data without explicit redaction policy.
- Do not make trace writes part of job execution correctness.
