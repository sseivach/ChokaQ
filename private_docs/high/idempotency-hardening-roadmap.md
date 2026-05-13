# Idempotency Hardening Roadmap

This file tracks the hardening work for ChokaQ's optional result-idempotency
plugin. It is intentionally private and not linked from the public documentation
navigation.

The current design direction is strong: idempotency is opt-in, split from core
execution, and separated into enqueue deduplication and result-level memory.
The current implementation, however, is a cache-after-execution marker. It does
not yet provide an atomic execution claim, so concurrent duplicates can still
run at the same time.

## Current State

ChokaQ currently has two idempotency layers:

- Level 1: enqueue deduplication through `IdempotencyKey` on active Hot jobs.
- Level 2: optional `IdempotencyMiddleware` for jobs implementing
  `IIdempotentJob`.

Implemented strengths:

- Idempotency is a plugin, not a core burden for every job.
- Jobs opt in explicitly through `IIdempotentJob`.
- Enqueue dedupe and result idempotency are documented as separate layers.
- The default in-memory store is clearly marked as not production-grade.
- Hosts can provide a custom `IIdempotencyStore`.
- Middleware placement is early in the execution pipeline.
- The current store supports a completed-result TTL.

Current gaps:

- `IIdempotencyStore` only supports `TryGetResultAsync` and
  `StoreResultAsync`.
- There is no atomic `TryBegin` or claim operation.
- There is no `InProgress` state.
- Two workers can both miss the cache, both execute the handler, and both store
  a completed marker.
- `IdempotencyMiddleware` uses `CancellationToken.None` because `IJobContext`
  does not currently expose the execution cancellation token.
- The stored payload is a completion marker, not a real handler result.
- TTL semantics can allow a duplicate to execute again after expiration.
- The in-memory store removes expired entries only when they are accessed, so a
  stream of one-off keys can grow memory until process restart.
- Partial side effects before handler failure are not protected by the cache.

Important correction:

- Current middleware already stores the completed marker only after `next()`
  returns successfully. If the handler throws, the marker is not written. An
  explicit `try/catch` can improve logging clarity, but it does not solve the
  harder case where the handler performed an external side effect and then
  failed before completion.

## Problem Statement

The current flow is:

```text
TryGetResult(key)
if found: skip
else:
    execute handler
    StoreResult(key)
```

This is best-effort completed-result caching. It is not an execution lock.

Race:

```text
Worker A: TryGetResult(key) -> null
Worker B: TryGetResult(key) -> null
Worker A: execute handler
Worker B: execute handler
Worker A: StoreResult(key)
Worker B: StoreResult(key)
```

Correct claim-based flow:

```text
TryBegin(key)
if already completed: skip
if already in progress: skip, wait, or reschedule
else:
    execute handler
    Complete(key)
```

## Design Principles

- ChokaQ should not promise exactly-once side effects.
- Idempotency should be framed as duplicate suppression, not magic.
- The result-idempotency plugin should provide atomic claim semantics for
  critical jobs.
- `InProgress` and `Completed` must be distinct states.
- In-progress claims need leases so crashed workers do not block a key forever.
- Store operations must observe execution cancellation when possible.
- TTL must be business-defined and explicitly documented.
- Failed executions should not poison the completed cache.
- Partial side effects remain the handler author's responsibility unless the
  business operation itself is idempotent.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until the claim-based contract is stable. |

## Executive Sequence

1. Preserve the current plugin architecture.
2. Rename or document the current middleware as completed-marker idempotency.
3. Add a claim-based idempotency store contract.
4. Add `InProgress`, `Completed`, and optionally `Failed` result states.
5. Add lease/expiration behavior for in-progress claims.
6. Update middleware flow to `TryBegin`, `Complete`, and `Release/Fail`.
7. Add Redis and SQL provider guidance.
8. Add cleanup policy for in-memory development store entries.
9. Add concurrency tests proving one handler execution per active key.
10. Update docs to state limits: at-least-once, not exactly-once.

## Phase A: Existing Architecture Baseline

Goal: keep the good architectural boundaries while hardening the guarantee.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Keep idempotency as an opt-in plugin. | Core job processing does not require result-idempotency storage. |
| A.2 | P0 | Done | Keep `IIdempotentJob`. | Jobs explicitly provide deterministic business keys. |
| A.3 | P0 | Done | Keep Level 1 enqueue dedupe. | Active Hot jobs can be deduped by `IdempotencyKey`. |
| A.4 | P0 | Done | Keep Level 2 middleware hook. | Optional middleware can short-circuit completed duplicate work. |
| A.5 | P1 | Done | Keep custom store support. | Hosts can provide Redis, SQL, or another store implementation. |
| A.6 | P1 | Partial | Document current guarantee honestly. | Comments explain at-least-once, but public docs should state that current Level 2 is a completed marker, not an execution claim. |

## Phase B: Atomic Claim Contract

Goal: prevent two workers from executing the same idempotent operation at the
same time.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Open | Add atomic begin operation. | Store exposes a method that claims a key only when it is absent or expired. |
| B.2 | P0 | Open | Add duplicate state result. | Middleware can distinguish claimed, already completed, and already in progress. |
| B.3 | P0 | Open | Add complete operation. | Successful handler execution transitions the claim to `Completed` with a result marker and TTL. |
| B.4 | P0 | Open | Add fail or release operation. | Failed handler execution releases or marks the claim according to policy. |
| B.5 | P1 | Open | Add in-progress lease. | Crashed workers do not hold a key forever. |
| B.6 | P1 | Open | Add lease-expired recovery semantics. | Another worker can claim an expired `InProgress` record predictably. |
| B.7 | P1 | Open | Preserve source compatibility plan. | Either version the interface or provide an adapter for existing `IIdempotencyStore` implementations. |

Candidate contract:

```csharp
public enum IdempotencyBeginStatus
{
    Started,
    AlreadyInProgress,
    AlreadyCompleted
}

public sealed record IdempotencyBeginResult(
    IdempotencyBeginStatus Status,
    string? ResultPayload = null,
    DateTimeOffset? LeaseExpiresAtUtc = null);

public interface IClaimBasedIdempotencyStore
{
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        string key,
        TimeSpan inProgressTtl,
        CancellationToken ct = default);

    ValueTask CompleteAsync(
        string key,
        string resultPayload,
        TimeSpan? resultTtl,
        CancellationToken ct = default);

    ValueTask ReleaseAsync(
        string key,
        CancellationToken ct = default);
}
```

## Phase C: In-Progress Semantics

Goal: make duplicate behavior explicit while the first execution is still
running.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P0 | Open | Add `InProgress` state. | Store records distinguish running work from completed work. |
| C.2 | P1 | Open | Decide duplicate behavior. | Middleware policy defines whether `InProgress` duplicates skip, wait, or reschedule. |
| C.3 | P1 | Open | Add configurable in-progress TTL. | Default lease is long enough for normal execution and shorter than indefinite lockout. |
| C.4 | P1 | Open | Add stale-claim telemetry. | Expired or stolen claims emit logs/metrics. |
| C.5 | P2 | Open | Add wait-with-timeout option. | Critical callers can wait briefly for a completed result before skipping or rescheduling. |

Recommended first behavior:

- `AlreadyCompleted`: skip handler.
- `AlreadyInProgress`: skip or reschedule without executing handler.
- `Started`: execute handler and complete on success.

Waiting for result can be added later. The first production-safe goal is to
avoid duplicate side effects.

## Phase D: Middleware Flow

Goal: update `IdempotencyMiddleware` from cache-after-execution to
claim-before-execution.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P0 | Partial | Middleware runs before handlers. | Current middleware is early in the pipeline, but it does not claim before execution. |
| D.2 | P0 | Open | Replace cache check with `TryBeginAsync`. | Concurrent duplicates cannot both enter `next()`. |
| D.3 | P0 | Open | Complete only after successful handler execution. | Failed handler runs do not write a completed marker. |
| D.4 | P1 | Open | Release claim on failure according to policy. | Transient failures can retry later without being blocked by stale `InProgress`. |
| D.5 | P1 | Open | Use execution cancellation token. | Store calls observe job shutdown/timeout once `IJobContext` exposes a token. |
| D.6 | P1 | Open | Add structured logs. | Logs distinguish started, completed, skipped completed, skipped in progress, released, and failed claim outcomes. |
| D.7 | P2 | Open | Add metrics. | Counters track idempotency claims, hits, in-progress skips, completions, releases, and store errors. |

Current blocker:

- `IJobContext` exposes `JobId` and `ReportProgressAsync`, but not a
  cancellation token. Middleware currently uses `CancellationToken.None`.

Candidate context addition:

```csharp
public interface IJobContext
{
    string JobId { get; }
    CancellationToken CancellationToken { get; }
    Task ReportProgressAsync(int percentage);
}
```

## Phase E: Store Providers

Goal: define practical implementations for common production stores.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P0 | Partial | Keep in-memory store for tests/dev. | Current store exists, but needs claim-state support. |
| E.2 | P1 | Open | Add claim-based in-memory store. | Uses atomic `ConcurrentDictionary` operations for `InProgress` and `Completed`. |
| E.3 | P1 | Open | Document Redis strategy. | Redis implementation uses atomic `SET key value NX EX ttl` or Lua for state transitions. |
| E.4 | P1 | Open | Document SQL strategy. | SQL implementation uses a unique key plus atomic insert/update transaction. |
| E.5 | P2 | Open | Add in-memory cleanup loop. | Expired entries are removed by a bounded background cleanup mechanism, not only on key access. |
| E.6 | P2 | Open | Add provider contract tests. | Any store implementation must pass common concurrency and TTL behavior tests. |

Redis shape:

```text
TryBegin:
  SET idempotency:{key} IN_PROGRESS NX EX {lease}

Complete:
  SET idempotency:{key} COMPLETED:{payload} XX EX {ttl}
```

SQL shape:

```sql
CREATE TABLE ChokaQIdempotency (
    [Key] varchar(255) NOT NULL PRIMARY KEY,
    [Status] tinyint NOT NULL,
    [ResultPayload] nvarchar(max) NULL,
    [LeaseExpiresAtUtc] datetime2(7) NULL,
    [CompletedAtUtc] datetime2(7) NULL,
    [ExpiresAtUtc] datetime2(7) NULL
);
```

In-memory cleanup note:

- Keep the in-memory provider explicitly non-production.
- Use a `PeriodicTimer` or similar bounded cleanup loop if long-running dev/test
  hosts can create many unique idempotency keys.
- Cleanup must not hide the need for Redis or SQL in multi-node production.

## Phase F: Result Payload Semantics

Goal: decide whether Level 2 stores a marker or a real result.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Partial | Store completion marker. | Current middleware stores `{ CompletedAt, JobId }`, but docs must call this a marker. |
| F.2 | P1 | Open | Rename docs from result cache to completion marker if no real result is returned. | Public language does not imply handlers return values that ChokaQ can replay. |
| F.3 | P2 | Deferred | Add typed result support. | Only after job handlers support return values or result capture in a formal contract. |
| F.4 | P2 | Open | Add custom payload hook. | Advanced hosts can store operation-specific metadata without changing handler signatures. |

Recommended first stance:

- Treat Level 2 as execution suppression with a completion marker.
- Do not claim that ChokaQ returns the same business result unless handler
  return values become part of the contract.

## Phase G: TTL And Business Policy

Goal: keep idempotency memory aligned with business risk.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P1 | Partial | Expose `ResultTtl`. | `IIdempotentJob.ResultTtl` exists, but docs need stronger policy guidance. |
| G.2 | P1 | Open | Add default TTL policy. | Hosts can set a default when jobs return null TTL. |
| G.3 | P1 | Open | Add minimum/maximum TTL validation. | Misconfigured TTLs fail or warn before production misuse. |
| G.4 | P1 | Open | Document business-defined TTL. | Examples explain payment, invoice, email, and webhook TTL tradeoffs. |
| G.5 | P2 | Open | Add expired-result telemetry. | Duplicates that execute because the result marker expired are visible in logs/metrics. |
| G.6 | P2 | Open | Add cleanup retention guidance. | Docs explain memory growth risk for many one-off keys and how cleanup interacts with TTL. |

Guidance:

- A one-hour TTL means the same business operation may execute again after one
  hour.
- Critical financial or externally visible operations usually need a longer
  business-defined retention window.
- Indefinite retention should be deliberate because it creates storage growth.

## Phase H: Failure And Poison-Cache Semantics

Goal: avoid hiding failed or partially completed work behind misleading
idempotency markers.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P0 | Done | Do not store marker before handler success. | Current middleware stores only after `next()` returns. |
| H.2 | P1 | Open | Release or expire in-progress claim on handler failure. | Failed attempts do not leave permanent `InProgress` records. |
| H.3 | P1 | Open | Document partial side effects. | Docs state that side effects before handler failure can still repeat under at-least-once delivery. |
| H.4 | P1 | Open | Add failure reason for idempotency store errors. | Store failures do not silently produce unsafe behavior. |
| H.5 | P2 | Open | Add policy for claim completion failure. | If handler succeeds but `CompleteAsync` fails, logs and retry behavior are explicit. |
| H.6 | P2 | Open | Add explicit failure logging around middleware flow. | Handler failure, claim release, and completed-marker writes are distinguishable in logs without caching failed attempts. |

Important limitation:

> Claim-based idempotency prevents concurrent duplicate execution for a key. It
> does not make non-idempotent external side effects exactly once if the process
> crashes after the side effect and before the completed marker is stored.

## Phase I: Tests

Goal: prove the edge cases that make idempotency credible.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P0 | Open | Add concurrent duplicate execution test. | Two simultaneous jobs with the same key result in one handler execution. |
| I.2 | P1 | Open | Add `InProgress` duplicate test. | Second worker sees `AlreadyInProgress` and does not run the handler. |
| I.3 | P1 | Open | Add completed duplicate test. | Completed marker prevents later duplicate execution before TTL expires. |
| I.4 | P1 | Open | Add TTL expiry test. | Duplicate execution is allowed after result TTL expires and this behavior is explicit. |
| I.5 | P1 | Open | Add lease expiry test. | A crashed in-progress claim can be reclaimed after lease expiration. |
| I.6 | P1 | Open | Add cancellation token test. | Store operations observe cancellation once context supports it. |
| I.7 | P2 | Open | Add in-memory cleanup test. | Expired keys are eventually removed even when they are never looked up again. |
| I.8 | P2 | Open | Add provider contract test suite. | In-memory, Redis, and SQL implementations must pass the same behavioral tests. |

## Suggested Implementation Order

1. Update docs/comments to describe the current middleware as a completed marker.
2. Add the claim-based store contract beside the current interface.
3. Implement claim-state support in the in-memory store.
4. Update middleware to use `TryBegin`, `Complete`, and `Release`.
5. Add execution cancellation token to `IJobContext`.
6. Add concurrency tests that prove one handler execution per active key.
7. Add in-memory cleanup for expired development-store entries.
8. Add Redis and SQL implementation guidance.
9. Add TTL policy docs and examples.
10. Decide whether to keep the old `IIdempotencyStore` as compatibility or mark
   it obsolete.

## Non-Goals

- Do not promise exactly-once delivery.
- Do not add distributed transactions with external systems.
- Do not make result idempotency mandatory for all jobs.
- Do not claim real result replay until handler return values are part of the
  public contract.
- Do not hide failed executions behind completed cache markers.
