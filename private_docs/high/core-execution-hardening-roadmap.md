# Core Execution Hardening Roadmap

This file tracks the hardening work from the core execution audit. It is
intentionally private and not linked from the public documentation navigation.

The audit is partially outdated: the most dangerous finding, stale worker
finalization without `WorkerId` ownership checks, is already fixed in the
current codebase. The remaining work is about making the execution contract more
explicit, reducing edge-case cancellation risk, and hardening type and payload
boundaries.

## Current State

ChokaQ already has a strong execution engine baseline:

- `JobProcessor` records queue lag before execution.
- Handler execution has a configurable timeout boundary.
- Heartbeats and zombie recovery protect against permanently stuck Processing
  jobs.
- Retry handling includes exponential backoff and jitter.
- Circuit breaker rejection prevents known-bad dependencies from consuming
  execution capacity.
- Failure taxonomy separates fatal, transient, throttled, timeout, cancelled,
  retry-exhausted, and zombie cases.
- Poison pills can fast-fail to DLQ instead of burning retry capacity.
- SQL and in-memory storage use atomic Hot, Archive, and DLQ transitions.
- Worker-owned finalization is guarded by `WorkerId` at the storage layer.
- `MarkAsProcessing` is the final ownership gate before user code runs.
- Profile-based `JobTypeRegistry` decouples persisted job keys from CLR type
  names for registered jobs.
- Enqueue idempotency is scoped to active Hot jobs, and result idempotency exists
  as optional middleware.

The remaining gaps:

- Heartbeat write failures still cancel running jobs after a configurable
  threshold, with a default of 3.
- Timeout and admin-triggered cancellation are still conflated in the execution
  catch path.
- There is no max job lifetime or retry budget TTL.
- Retry calculation is built into the core processor/options flow rather than a
  pluggable strategy contract.
- Fallback type identity still uses CLR short names in some paths.
- In-memory requeue fallback can still scan loaded assemblies.
- JSON serialization is not centralized behind shared options or a serializer
  contract.
- Enqueue-side payload size limits are not a formal core boundary.
- README and release docs should state the execution contract more explicitly.

## Design Principles

- The system provides at-least-once execution, not exactly-once execution.
- User handlers must be idempotent when side effects matter.
- Storage-level ownership is the source of truth for finalization authority.
- Heartbeat failure should be treated as a signal of storage or network pressure,
  not immediately as proof that user code is unsafe.
- Retry count and retry lifetime are separate safety limits.
- Persisted job type identity should be stable across refactors and assemblies.
- Serialization behavior must be a deliberate compatibility contract.
- Every limit should fail predictably before jobs are accepted or executed.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until core safety gaps are closed. |

## Executive Sequence

1. Preserve current ownership guarantees and add any missing regression coverage.
2. Make heartbeat failure handling less destructive.
3. Split timeout cancellation from admin cancellation in persisted taxonomy.
4. Add max job lifetime or retry budget TTL.
5. Decide whether retry policy needs a public strategy abstraction.
6. Remove short-name type fallbacks from production paths.
7. Centralize JSON serialization options and document compatibility rules.
8. Add core payload and metadata limits.
9. Make execution guarantees explicit in README and release docs.

## Phase A: Core Execution Baseline

Goal: record the already-strong engine pieces so future work does not regress
them.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Record queue lag before execution. | `JobProcessor` emits queue lag using created/scheduled time and current execution time. |
| A.2 | P0 | Done | Keep execution timeout configurable. | Handler execution timeout comes from `ChokaQOptions.Execution.DefaultTimeout` or per-queue overrides. |
| A.3 | P0 | Done | Keep retry backoff and jitter. | Transient failures schedule delayed retries with bounded backoff and jitter. |
| A.4 | P0 | Done | Keep circuit breaker rejection path. | Open circuits reschedule jobs without executing handlers. |
| A.5 | P0 | Done | Keep poison-pill fast-fail. | Fatal errors move directly to DLQ with a fatal failure reason. |
| A.6 | P1 | Done | Keep failure taxonomy. | DLQ rows carry machine-readable failure reasons for triage. |
| A.7 | P1 | Done | Keep active-worker metrics. | Processor increments and decrements active worker gauge around execution. |

## Phase B: Worker Ownership And Double Execution Safety

Goal: prevent stale workers from finalizing jobs after ownership has moved.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Done | Propagate `workerId` into finalization. | Success, failure, cancellation, and retry transitions receive the current worker ID. |
| B.2 | P0 | Done | Guard success archive by owner. | SQL `ArchiveSucceeded` only moves rows still owned by the current worker or explicit admin path. |
| B.3 | P0 | Done | Guard DLQ moves by owner. | SQL failure, timeout, cancellation, and zombie worker paths do not let stale workers move someone else's row. |
| B.4 | P0 | Done | Guard retry reschedule by owner. | Retry reschedule does not clear another worker's lease. |
| B.5 | P0 | Done | Treat zero-row finalization as lost ownership. | State manager logs and suppresses false notifications when storage returns false. |
| B.6 | P0 | Done | Use `MarkAsProcessing` as execution lease gate. | Prefetched stale copies do not dispatch user code. |
| B.7 | P1 | Done | Add stale-worker race tests. | Integration coverage proves stale workers cannot archive or retry reclaimed jobs. |
| B.8 | P1 | Open | Emit ownership-conflict telemetry. | Zero-row worker-owned finalization increments a metric or structured counter. |

Target framing:

> Storage-level ownership guarantees that only the current lease holder can
> finalize a job. A stale worker becomes a zero-row no-op instead of producing a
> false Archive or DLQ transition.

## Phase C: Heartbeat And Cancellation Policy

Goal: make heartbeat and cancellation behavior safer under temporary SQL or
network pressure.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Partial | Make heartbeat failure threshold configurable. | `Execution.HeartbeatFailureThreshold` exists and is validated, but the default is still aggressive for production storage stalls. |
| C.2 | P1 | Open | Raise default heartbeat failure threshold. | Default threshold moves from 3 to a less aggressive value such as 5 or 10, with docs explaining the tradeoff. |
| C.3 | P1 | Open | Add degraded heartbeat mode. | Repeated heartbeat failures can mark the job or worker as suspect/degraded before cancelling execution. |
| C.4 | P1 | Partial | Guard cancellation finalization by ownership. | Storage guards already prevent stale moves, but timeout and admin cancellation still share the same execution catch path. |
| C.5 | P1 | Open | Separate admin cancellation from execution timeout. | Admin cancellation records `Cancelled`; timeout records `Timeout`; logs and UI can distinguish both. |
| C.6 | P2 | Open | Add cancellation race tests. | Tests prove cancel-after-success and cancel-during-finalize do not create false DLQ rows. |
| C.7 | P2 | Open | Add heartbeat pressure runbook. | Docs explain what to inspect when heartbeat failures rise: SQL latency, locks, network, timeout settings, and worker load. |

Recommended default direction:

- Do not immediately treat 3 missed heartbeat writes as proof that user code
  should be cancelled.
- Prefer warning, degraded state, and operator visibility before destructive
  execution cancellation.
- Keep zombie rescue as the authoritative cleanup path for truly abandoned
  Processing jobs.

## Phase D: Retry Lifetime And Job TTL

Goal: prevent jobs from living indefinitely because retry count and retry delay
alone do not bound wall-clock lifetime.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P0 | Done | Bound retry attempts. | `Retry.MaxAttempts` controls total execution attempts. |
| D.2 | P1 | Done | Bound individual retry delay. | `Retry.MaxDelay` caps calculated and throttled retry delays. |
| D.3 | P1 | Open | Add max job age. | Jobs older than `Retry.MaxJobAge` or `Execution.MaxJobAge` move to DLQ instead of retrying forever. |
| D.4 | P1 | Open | Add max retry window. | Retry scheduling stops when the next delay would exceed the job's lifetime budget. |
| D.5 | P1 | Open | Add failure reason for lifetime expiry. | DLQ can distinguish retry exhaustion from age/TTL expiration. |
| D.6 | P2 | Open | Add per-queue TTL override. | Long-running batch queues can use different max-age budgets than user-facing queues. |
| D.7 | P2 | Open | Add TTL tests. | Tests cover age-expired retry, throttled retry-after exceeding budget, and per-queue overrides. |
| D.8 | P2 | Open | Consider `IRetryStrategy`. | Hosts can replace retry delay calculation without forking `JobProcessor`, while default behavior remains stable. |

Candidate options:

```csharp
public sealed class ChokaQRetryOptions
{
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan? MaxJobAge { get; set; } = TimeSpan.FromDays(1);
}
```

Optional retry strategy shape:

```csharp
public interface IRetryStrategy
{
    RetryDecision GetNextRetry(RetryContext context);
}
```

## Phase E: Type Identity And Resolution

Goal: make persisted job type identity stable, cheap to resolve, and safe across
assemblies.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P0 | Done | Add `JobTypeRegistry`. | Registered profile keys resolve to CLR job types without assembly scanning. |
| E.2 | P0 | Done | Use registry keys for SQL enqueue. | SQL mode stores configured profile keys when available. |
| E.3 | P1 | Done | Use registry keys for in-memory enqueue and dispatch. | In-memory Bus mode uses the same persisted key model as SQL mode for registered jobs. |
| E.4 | P1 | Partial | Remove short-name collision risk. | Registered jobs are safe, but fallback paths still use `job.GetType().Name` or `t.Name == job.Type`. |
| E.5 | P1 | Open | Replace fallback short names. | Unregistered fallback type identity uses `FullName`, `AssemblyQualifiedName`, or explicit rejection. |
| E.6 | P1 | Open | Remove assembly scan fallback from hot paths. | In-memory DLQ requeue does not call `AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetTypes)` per operation. |
| E.7 | P2 | Open | Add duplicate short-name tests. | Tests prove two jobs with the same class name in different namespaces cannot collide. |
| E.8 | P2 | Open | Document stable type-key policy. | Docs recommend explicit profile keys for persisted jobs and explain migration impact when keys change. |

Preferred direction:

- Treat explicit profile keys as the production path.
- Either reject unregistered Bus jobs or persist a fully qualified type identity.
- Keep a startup-built dictionary for any fallback resolution, not per-job
  reflection scans.

## Phase F: Serialization Contract

Goal: make payload serialization predictable across versions and hosts.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Open | Add centralized JSON options. | Enqueue, dispatch, in-memory requeue, and idempotency middleware share configured `JsonSerializerOptions`. |
| F.2 | P1 | Open | Add serializer abstraction. | Hosts can replace JSON behavior through a small ChokaQ serializer contract if needed. |
| F.3 | P1 | Open | Document payload compatibility. | Docs explain additive fields, removed fields, enum handling, casing, nullable changes, and versioned job DTOs. |
| F.4 | P1 | Open | Add versioned payload guidance. | Recommended pattern uses explicit job type keys such as `email.send.v1` and new keys for incompatible schema changes. |
| F.5 | P2 | Open | Add serialization tests. | Tests cover configured options, old payload deserialization, unknown fields, and enum/string policy. |
| F.6 | P2 | Open | Add payload validation hook. | Optional hook validates serialized payload before storage accepts it. |

Candidate contract:

```csharp
public interface IChokaQJobSerializer
{
    string Serialize(IChokaQJob job, Type jobType);
    IChokaQJob Deserialize(string payload, Type jobType);
}
```

## Phase G: Runtime Limits And Boundaries

Goal: make resource limits explicit before a host reaches production volume.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P0 | Done | Bound retry attempts. | Invalid retry attempts fail configuration validation. |
| G.2 | P0 | Done | Bound execution timeout. | Invalid execution timeout fails configuration validation. |
| G.3 | P1 | Done | Bound in-memory capacity. | In-memory queue capacity is finite and validated. |
| G.4 | P1 | Partial | Bound payload size. | The Deck validates edited payload size, but core enqueue paths do not expose a configurable max payload size. |
| G.5 | P1 | Open | Add enqueue payload size limit. | Enqueue rejects payloads larger than `Execution.MaxPayloadBytes` or `Serialization.MaxPayloadBytes`. |
| G.6 | P1 | Open | Add metadata size limits. | Queue, type key, tags, actor, and idempotency key limits are documented and consistently validated across storage modes. |
| G.7 | P2 | Open | Add limit reference docs. | README or configuration docs list default limits and failure modes. |
| G.8 | P2 | Open | Add boundary tests. | Tests cover max payload, type key length, queue length, actor length, tags, retry values, and timeout values. |

## Phase H: Public Execution Contract

Goal: make ChokaQ's guarantees clear enough that users do not infer exactly-once
semantics.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P0 | Done | Document at-least-once internally. | Roadmap, study guide, and lifecycle docs reference at-least-once execution and idempotency. |
| H.2 | P1 | Partial | Make README contract explicit. | README mentions idempotency, but should include a concise "Delivery Guarantees" section. |
| H.3 | P1 | Open | Add idempotent handler checklist. | Docs show how to design handlers for external side effects: idempotency keys, unique constraints, outbox, and safe retries. |
| H.4 | P1 | Open | Document zombie semantics. | Docs explain why Processing zombies go to DLQ instead of automatic retry. |
| H.5 | P2 | Open | Document ownership guarantees. | Public docs state that stale finalization is blocked at storage level but side effects inside handlers still require idempotency. |
| H.6 | P2 | Open | Add "what ChokaQ does not guarantee". | Docs explicitly reject exactly-once side-effect guarantees. |

Suggested README section:

```markdown
## Delivery Guarantees

ChokaQ provides durable enqueue and at-least-once execution. A job can execute
more than once if a worker crashes or loses ownership after user code has already
performed side effects but before finalization is committed. Storage ownership
guards prevent stale workers from falsely finalizing jobs, but handlers must be
idempotent for external side effects.
```

## Phase I: Verification

Goal: prove the edge cases that make or break production confidence.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P0 | Done | Add SQL stale-worker race tests. | Current integration coverage proves stale finalization cannot win after ownership moves. |
| I.2 | P1 | Open | Add heartbeat failure policy tests. | Tests cover warn/degraded/cancel thresholds and confirm behavior under transient storage failures. |
| I.3 | P1 | Open | Add admin cancellation race tests. | Tests cover cancel while running, cancel after success, and cancel during finalization. |
| I.4 | P1 | Open | Add max job age tests. | Tests prove old jobs stop retrying and move to DLQ with the expected reason. |
| I.5 | P1 | Open | Add type collision tests. | Tests prove duplicate CLR short names cannot resolve incorrectly. |
| I.6 | P1 | Open | Add serializer compatibility tests. | Tests prove configured serializer options apply consistently across enqueue and dispatch. |
| I.7 | P2 | Open | Add payload boundary tests. | Tests prove oversized payloads fail before storage mutation. |

## Suggested Implementation Order

1. Add a README delivery-guarantees section.
2. Raise or redesign heartbeat failure behavior.
3. Split admin cancellation from timeout cancellation.
4. Add max job age and lifetime-expired DLQ taxonomy.
5. Decide whether `IRetryStrategy` belongs in the public API or should remain
   an internal extension point.
6. Remove short-name fallback type identity or make it fully qualified.
7. Add centralized JSON options or serializer abstraction.
8. Add core payload and metadata limits.
9. Add targeted race, TTL, type-collision, and serializer tests.

## Non-Goals

- Do not promise exactly-once side effects.
- Do not add distributed transactions for external dependencies.
- Do not make automatic zombie retry the default for Processing jobs.
- Do not build a full schema migration framework for user payloads.
- Do not make DB maintenance or purge UI part of this roadmap; that is tracked
  separately in the DB Maintenance roadmap.
