# SQL Storage Engine Hardening Roadmap

This file tracks hardening work for the SQL Server storage implementation. It
is intentionally private and not linked from the public documentation
navigation.

The audit is partly outdated. The most serious concern in the note, missing
transactions around Hot, Archive, and DLQ moves, is already fixed in the current
codebase. Critical state transitions now use `BEGIN TRANSACTION`, `XACT_ABORT`,
`DELETE ... OUTPUT`, and rollback-on-catch patterns.

The remaining work is mostly about reducing stringly typed SQL risk, removing
runtime reflection in frequent paths, improving large-batch ID handling,
reviewing SQL Server `MERGE` usage, and splitting `SqlJobStorage` into clearer
ownership boundaries.

## Current State

ChokaQ SQL storage already has a strong production-preview baseline:

- Three Pillars schema: `JobsHot`, `JobsArchive`, and `JobsDLQ`.
- SQL fetch uses `UPDLOCK` and `READPAST` for competing consumers.
- Fetch respects per-queue bulkhead limits through SQL-side active counts.
- Hot to Archive, Hot to DLQ, DLQ to Hot, zombie rescue, batch cancellation,
  filtered DLQ requeue, and cleanup paths use transaction boundaries.
- Core transitions use `DELETE ... OUTPUT` buffers so moves are atomic.
- Worker ownership guards prevent stale finalization.
- Retry policy is centralized in `SqlRetryPolicy`.
- Command timeout is centralized through `SqlMapper`.
- Most SQL storage public methods pass `CancellationToken` through connection
  open, command execution, and retry delays.
- `TypeMapper` caches reflection metadata for row mapping.
- `TypeMapper` does not currently compile row mappers; it still uses
  reflection-based construction, property lookup, and property assignment per
  row.
- `NOLOCK` is explicitly limited to dashboard/telemetry reads, while fetch and
  mutation paths use stronger consistency.

Remaining risks:

- Selected-ID batch operations still serialize IDs as JSON and parse with
  `OPENJSON`.
- SQL templates use string placeholders and `.Replace(...)`.
- Filter predicates are built by string concatenation, although values are
  still parameterized.
- SQL Server `MERGE` is used for stats, metric buckets, and queue config
  upserts. It is convenient, but it deserves a concurrency review because
  `MERGE` has a long SQL Server footgun history.
- `UPDLOCK` plus `READPAST` is the right competing-consumer primitive, but
  multi-worker fairness across queues and priorities needs load tests.
- `MergeParams` uses reflection per call.
- `ParameterBuilder` uses uncached reflection for anonymous parameter objects.
- `TypeMapper` caches metadata but not compiled materializers, so high-volume
  read paths can still pay reflection and column lookup cost per row.
- `SqlJobStorage` is large and owns many unrelated responsibilities.
- Enqueue idempotency uses pre-check plus insert plus duplicate-key recovery,
  which can be optimized later.
- Open-per-method connections rely on ADO.NET pooling, which is acceptable now
  but should be measured before high-throughput claims.
- A few `CancellationToken.None` call sites exist outside `SqlJobStorage` for
  shutdown cleanup and idempotency middleware.

## Design Principles

- Storage is a correctness boundary first and a performance boundary second.
- State transitions must be single database transactions.
- SQL text generation should be explicit, testable, and hard to misuse.
- Operator queries may be approximate only when the code and docs say so.
- Hot-path and bulk paths should avoid repeated reflection and avoid avoidable
  JSON parsing.
- Connection pooling is acceptable, but throughput claims must be measured.
- Large classes should be split by ownership once behavior is stable.

## Status Legend

| Status | Meaning |
|---|---|
| Done | Implemented, tested, and documented for current scope. |
| Partial | Some behavior exists, but the acceptance criteria below are not complete. |
| Open | Not implemented yet. |
| Deferred | Intentionally postponed until higher-risk items are handled. |

## Executive Sequence

1. Preserve existing atomic transition guarantees.
2. Audit all remaining `CancellationToken.None` paths and document intentional
   shutdown exceptions.
3. Replace selected-ID JSON payloads with a table-valued or temp-table path.
4. Replace reflection-based parameter merging with dictionary or cached
   parameter APIs.
5. Encapsulate SQL predicate and template building.
6. Review `MERGE` use and replace it where high-concurrency upserts are safer
   as explicit update-then-insert operations.
7. Add fetch fairness and starvation tests for multi-worker SQL polling.
8. Split `SqlJobStorage` into smaller operational areas.
9. Add performance tests for batch IDs, parameter building, and high-frequency
   dashboard/storage queries.
10. Optimize enqueue idempotency after correctness and clarity are stable.

## Phase A: Atomic Transition Guarantees

Goal: keep the storage engine's core data-integrity guarantees intact.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| A.1 | P0 | Done | Use atomic Hot to Archive move. | Success archive uses transaction, `DELETE ... OUTPUT`, archive insert, stats update, and rollback on failure. |
| A.2 | P0 | Done | Use atomic Hot to DLQ move. | Failure, timeout, cancellation, and zombie moves are transaction-scoped. |
| A.3 | P0 | Done | Use atomic DLQ to Hot move. | Single and batch resurrection delete from DLQ and insert into Hot in one transaction. |
| A.4 | P0 | Done | Use atomic retry reschedule. | Retry scheduling updates Hot and stats in a transaction. |
| A.5 | P0 | Done | Use atomic zombie rescue. | Zombie rescue moves rows to DLQ and updates stats in a transaction. |
| A.6 | P0 | Done | Preserve ownership guards. | Worker-owned finalization paths filter by `WorkerId` and return zero-row no-op on stale ownership. |
| A.7 | P1 | Open | Add transaction-shape regression tests. | Tests prove crash-style failures inside archive/DLQ/resurrection transactions do not lose or duplicate rows. |

Audit note:

The original claim that `INSERT INTO JobsArchive` and `DELETE FROM JobsHot` are
not transaction protected is no longer true for the current code.

## Phase B: Cancellation And Shutdown Boundaries

Goal: make cancellation behavior explicit across storage, worker shutdown, and
idempotency middleware.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| B.1 | P0 | Done | Pass tokens through SQL storage methods. | `SqlJobStorage` opens connections and executes commands with the caller token. |
| B.2 | P0 | Done | Pass tokens through SQL retry delays. | `SqlRetryPolicy` observes cancellation during transient retry backoff. |
| B.3 | P1 | Partial | Audit `CancellationToken.None` usage. | Remaining usages are intentional, bounded, or replaced. |
| B.4 | P1 | Open | Add bounded shutdown release token. | Prefetched job release during shutdown uses a short dedicated timeout instead of unbounded `CancellationToken.None`. |
| B.5 | P1 | Open | Fix idempotency middleware token propagation. | Idempotency store calls use an execution token once `IJobContext` exposes one. |
| B.6 | P2 | Open | Add cancellation tests for SQL commands. | Tests prove command cancellation, retry cancellation, and shutdown release behavior. |

Current nuance:

- `SqlJobStorage` itself is mostly token-correct.
- `CancellationToken.None` in shutdown release paths is intended to avoid leaving
  prefetched jobs claimed after the host token is already canceled, but it should
  still be bounded.
- Idempotency middleware token propagation is tracked more deeply in the
  idempotency roadmap.

## Phase C: Connection Lifecycle And Throughput

Goal: keep the current simple connection-per-operation model while measuring
when it becomes a bottleneck.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| C.1 | P1 | Done | Use ADO.NET connection pooling. | Each method opens/disposes `SqlConnection`, relying on the provider pool. |
| C.2 | P1 | Done | Centralize command timeout. | `OpenConnectionAsync` stores timeout policy for `SqlMapper` command creation. |
| C.3 | P2 | Open | Add connection-pool pressure metrics or logs. | Operators can distinguish storage latency from pool exhaustion. |
| C.4 | P2 | Open | Add high-throughput benchmark. | Benchmark measures connection open, fetch, finalize, and dashboard query costs at target rates. |
| C.5 | P2 | Deferred | Add connection factory abstraction. | Only introduce if tests show connection lifecycle overhead or pool settings need central control. |
| C.6 | P2 | Deferred | Add operation batching. | Only introduce for proven high-throughput bottlenecks. |

Position:

Open-per-method is acceptable for the current production-preview target. It
should not be called a bug without workload data, but it is a scale boundary to
measure.

## Phase D: SQL Template And Predicate Building

Goal: reduce runtime string-template risk while preserving parameterized values.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| D.1 | P1 | Partial | Keep dynamic values parameterized. | Current dynamic filters append SQL fragments but pass user values as parameters. |
| D.2 | P1 | Open | Replace placeholder `.Replace(...)` for known variants. | Fetch, history, and DLQ bulk queries use explicit builder methods or precompiled variants. |
| D.3 | P1 | Open | Add a small SQL predicate builder. | Filter builders return SQL fragments and parameters through one tested API. |
| D.4 | P1 | Open | Validate template placeholders at startup. | Missing or misspelled placeholders fail fast. |
| D.5 | P2 | Open | Add SQL generation tests. | Tests assert generated SQL and parameters for each filter combination. |
| D.6 | P2 | Open | Consider Dapper-style builder or local equivalent. | Only if the local builder starts becoming complex. |

Important distinction:

The current dynamic SQL is not raw user interpolation; values remain
parameterized. The risk is maintainability and template fragility, not obvious
SQL injection in the reviewed paths.

## Phase E: Batch ID Handling

Goal: remove JSON parsing as the scalability limit for large selected-ID
operations.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| E.1 | P1 | Partial | Keep selected-ID operations bounded. | Selected purge loops through `CleanupBatchSize` transactions, but IDs are still passed as JSON. |
| E.2 | P1 | Open | Replace JSON IDs for large batches. | SQL Server path supports TVP, temp table plus bulk copy, or equivalent indexed strategy. |
| E.3 | P1 | Open | Keep JSON path for small batches if useful. | Small ID lists can keep simpler JSON behavior behind a size threshold. |
| E.4 | P1 | Open | Add 100k-ID benchmark or integration test. | Large selected DLQ purge/requeue avoids repeated JSON parsing and remains stable. |
| E.5 | P2 | Open | Share batch-ID infrastructure. | Purge, resurrection, and cancellation use the same ID input strategy. |

Current JSON ID paths:

- `ResurrectBatchAsync`
- `ArchiveCancelledBatchAsync`
- `PurgeDLQAsync`

Preferred path:

- Small batch: current JSON path is acceptable.
- Large batch: `#TempIds` plus bulk insert, then bounded `DELETE` or move by
  indexed join.

## Phase F: Reflection And Parameter Mapping

Goal: remove repeated reflection from frequent query execution paths.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| F.1 | P1 | Done | Cache row-mapper reflection. | `TypeMapper` caches properties and constructors. |
| F.2 | P1 | Partial | Use dictionary parameters in filter builders. | Filter builders already return dictionaries, but helper merges still reflect over anonymous objects. |
| F.3 | P1 | Open | Replace `MergeParams` reflection. | `MergeParams` accepts dictionaries or key/value spans instead of reflecting over anonymous objects. |
| F.4 | P1 | Open | Cache `ParameterBuilder` reflection. | Anonymous parameter property metadata is cached by type. |
| F.5 | P2 | Open | Add compiled row materializers. | Frequently used read models map rows through cached delegates or generated code instead of per-row reflection. |
| F.6 | P2 | Open | Cache column ordinals per result shape. | Mapping avoids repeated case-insensitive property/column scans for every row. |
| F.7 | P2 | Open | Add parameter-builder benchmark. | Benchmark shows reduced allocations and reflection calls for common query paths. |
| F.8 | P3 | Deferred | Consider source-generated mappers. | Only if compiled delegates are not enough for high-volume read models. |

Current risk:

`second.GetType().GetProperties()` in `MergeParams` is a smaller problem than
full AppDomain type scanning, but it can still show up in high-frequency
dashboard or history pagination paths.

Mapper nuance:

`TypeMapper` already caches metadata, so the problem is not repeated
`GetProperties` for every row. The remaining cost is reflective construction,
property assignment, and repeated column/property matching while materializing
many rows.

## Phase G: Read Consistency And NOLOCK

Goal: make approximate reads deliberate and prevent dirty reads from leaking
into correctness paths.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| G.1 | P0 | Done | Avoid `NOLOCK` in fetch and mutation paths. | Fetch, ownership, state moves, recovery, and admin mutations do not depend on dirty reads. |
| G.2 | P1 | Partial | Limit `NOLOCK` to telemetry. | Current comments state dashboard reads trade consistency for observer impact. |
| G.3 | P1 | Open | Add read-consistency policy docs. | Docs list which reads are approximate and which must be authoritative. |
| G.4 | P2 | Open | Add tests or checks for `NOLOCK` placement. | Tests or code review checks prevent `NOLOCK` entering correctness paths. |
| G.5 | P2 | Open | Document dirty-read artifacts. | Dashboard docs explain that approximate reads can duplicate, miss, or temporarily contradict rows under load. |
| G.6 | P2 | Deferred | Consider snapshot isolation guidance. | Only after deployment guidance includes database isolation tradeoffs. |

Current position:

`NOLOCK` is not used "everywhere" in the current storage engine. It is used in
dashboard-style reads where approximate values are acceptable, but this policy
should be documented more clearly.

## Phase H: Enqueue Idempotency Roundtrips

Goal: keep enqueue idempotency correct first, then reduce roundtrips when scale
requires it.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| H.1 | P0 | Done | Use unique Hot index for enqueue idempotency. | Duplicate active Hot work is rejected by the database. |
| H.2 | P0 | Done | Recover from duplicate-key race. | Duplicate insert races return the winning job ID after SQL 2601/2627. |
| H.3 | P2 | Open | Optimize to one primary roundtrip. | Enqueue path avoids pre-check for common insert success when benchmarks justify it. |
| H.4 | P2 | Open | Add contention benchmark. | Benchmark compares pre-check-plus-insert against insert-first recovery under duplicate-key contention. |
| H.5 | P2 | Deferred | Add insert-output/upsert variant. | Only if contention or throughput tests justify extra SQL complexity. |

Position:

The current approach is correct. It can be made cheaper later, but it is not a
production blocker at the current target scale.

## Phase I: SQL Server MERGE And Upserts

Goal: keep stats and config upserts correct under SQL Server concurrency quirks.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| I.1 | P1 | Partial | Use `MERGE` for stats and queue upserts. | Current code uses `MERGE` in initialization, stats updates, metric buckets, and queue config paths. |
| I.2 | P1 | Open | Audit each `MERGE` statement. | Each usage is classified as safe enough, replaceable, or requiring extra locking. |
| I.3 | P1 | Open | Replace high-concurrency `MERGE` paths if needed. | Stats and metric bucket updates can use explicit `UPDATE` then `INSERT` with duplicate-key retry where safer. |
| I.4 | P1 | Open | Add concurrent stats update tests. | Parallel success, failure, retry, and metric bucket writes do not lose counts or throw avoidable upsert races. |
| I.5 | P2 | Open | Document SQL Server upsert policy. | Future storage changes know when `MERGE` is allowed and when explicit upsert is preferred. |

Position:

`MERGE` is not automatically wrong, but it should not be treated as a free
atomicity upgrade in high-concurrency SQL Server paths. Audit and test before
changing working code.

## Phase J: Fetch Fairness And Queue Skew

Goal: prove that competing SQL workers do not create unacceptable queue or
priority starvation.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| J.1 | P1 | Done | Use `UPDLOCK` and `READPAST` for work claiming. | Competing workers skip locked rows instead of blocking each other. |
| J.2 | P1 | Partial | Respect SQL-side queue bulkheads. | Fetch uses per-queue active counts, but fairness under mixed queues needs load tests. |
| J.3 | P1 | Open | Add multi-worker queue fairness test. | High-volume queues do not starve lower-volume eligible queues beyond documented policy. |
| J.4 | P1 | Open | Add priority/scheduled-time starvation test. | Priority and scheduled-time ordering remain acceptable with multiple workers and locked rows. |
| J.5 | P2 | Open | Document fairness policy. | Docs state whether ChokaQ guarantees strict fairness or best-effort scheduling under SQL concurrency. |

## Phase K: Class Decomposition

Goal: split `SqlJobStorage` into maintainable components without changing
behavior.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| K.1 | P1 | Open | Extract core job commands. | Enqueue, fetch, mark processing, keepalive, and release move out of the large class. |
| K.2 | P1 | Open | Extract state transition commands. | Archive, DLQ, retry, resurrection, cancellation, and zombie moves have a focused owner. |
| K.3 | P1 | Open | Extract admin operations. | Edit, purge, filtered bulk operations, and queue management are grouped separately. |
| K.4 | P1 | Open | Extract read models. | Dashboard, history, and inspector queries are grouped separately. |
| K.5 | P2 | Open | Keep `IJobStorage` facade. | Public DI still exposes one `IJobStorage`; internals compose smaller collaborators. |
| K.6 | P2 | Open | Add focused tests per component. | Each extracted component has tests around its SQL and parameter behavior. |

Suggested split:

- `SqlJobCommandStore`
- `SqlJobTransitionStore`
- `SqlJobAdminStore`
- `SqlJobReadStore`
- `SqlQueueConfigStore`
- `SqlMaintenanceStore`

## Phase L: Verification

Goal: prove storage behavior under load and failure.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| L.1 | P1 | Open | Add SQL generation snapshot tests. | Query builders produce expected fragments and parameters. |
| L.2 | P1 | Open | Add large-batch ID tests. | 100k selected IDs do not rely on repeated JSON parsing in the optimized path. |
| L.3 | P1 | Open | Add cancellation tests. | Canceled tokens stop open, execute, and retry delay paths. |
| L.4 | P1 | Open | Add transaction rollback tests. | Simulated statement failure rolls back source row moves. |
| L.5 | P1 | Open | Add `MERGE` concurrency tests. | Stats and metric bucket upserts remain correct under parallel transitions. |
| L.6 | P1 | Open | Add fetch fairness tests. | Multi-worker fetch does not produce unacceptable queue or priority starvation. |
| L.7 | P2 | Open | Add connection-pool benchmark. | Measures open-per-call overhead under burst workloads. |
| L.8 | P2 | Open | Add mapper and parameter-builder benchmark. | Measures reflection and allocation improvements after caching or compiled mapping. |

## Suggested Implementation Order

1. Audit and bound remaining `CancellationToken.None` paths.
2. Replace `MergeParams` reflection and cache `ParameterBuilder` metadata.
3. Add SQL generation tests for current dynamic filters.
4. Introduce a small predicate/template builder and remove fragile
   `.Replace(...)` paths incrementally.
5. Add temp-table or TVP path for large selected-ID operations.
6. Audit `MERGE` usage and add concurrency tests before replacing it.
7. Add fetch fairness and starvation tests for multi-worker SQL polling.
8. Add rollback/cancellation/load tests.
9. Split `SqlJobStorage` behind the existing `IJobStorage` facade.
10. Benchmark enqueue idempotency and optimize only if it shows up.

## Non-Goals

- Do not replace SQL Server as the primary storage provider in this roadmap.
- Do not remove transactions around state moves.
- Do not optimize enqueue idempotency before preserving correctness tests.
- Do not add a heavy ORM just to avoid small SQL builders.
- Do not make approximate dashboard reads authoritative.
