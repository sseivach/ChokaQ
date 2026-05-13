# ChokaQ Production Hardening Mega-Roadmap

This document is the working roadmap for taking ChokaQ from proof-of-concept to a production-grade background job processor.

It merges two planning tracks:

- Core correctness roadmap: durability, SQL atomicity, worker lifecycle, security, CI, release readiness.
- Operational hardening roadmap: observability, failure transparency, worker ownership safety, and operator controls.

## North Star

ChokaQ should provide a SQL-backed background job framework with a minimal platform dependency footprint and honest, testable guarantees:

- Durable enqueue.
- At-least-once execution semantics.
- Atomic state transitions between Hot, Archive, and DLQ.
- Safe worker ownership and zombie recovery.
- Secure admin dashboard by default.
- Clear operational visibility into saturation, failures, and recovery.
- Reproducible build, test, and release pipeline.

## Priority Legend

| Priority | Meaning |
|---|---|
| P0 | Correctness or data-integrity blocker. Must be fixed before production preview. |
| P1 | High production risk, security risk, or major operational gap. |
| P2 | Product maturity, ergonomics, maintainability, and release quality. |

## Status Legend

| Status | Meaning |
|---|---|
| Open | Not implemented yet. |
| Partial | Some support exists, but acceptance criteria are not met. |
| In Progress | Active work is underway. |
| Done | Implemented, tested, and documented. |
| Frozen | Intentionally paused and skipped in the active execution sequence. |

## Executive Sequence

1. Freeze current behavior and split tests.
2. Fix SQL enqueue mode and worker lifecycle.
3. Make SQL transitions atomic.
4. Add worker ownership guards.
5. Fix lease, prefetch, zombie, and bulkhead race conditions.
6. Harden The Deck security.
7. Add operational observability: queue lag, throughput, failure rate, top errors. Done.
8. Add failure taxonomy and DLQ badges. Done.
9. Complete bulk operational controls.
10. Package, document, and release.

## Phase 0: Freeze And Baseline

Goal: establish a stable engineering baseline before changing core semantics.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 0.1 | P1 | Done | Add test categories for Unit and Integration. | `Category=Unit` runs without Docker; `Category=Integration` runs SQL/Testcontainers tests explicitly. |
| 0.2 | P1 | Done | Add CI for build and unit tests. | `.github/workflows/ci.yml` restores, builds, and runs `Category=Unit` on pull requests and pushes. |
| 0.3 | P1 | Done | Add SQL integration CI with SQL Server container/service. | `.github/workflows/ci.yml` runs `Category=Integration` separately with Docker/Testcontainers on supported runners. |
| 0.4 | P2 | Done | Track known issues in this roadmap. | Remaining open/partial work has roadmap IDs and status; no untracked P0/P1 finding remains. |
| 0.5 | P2 | Done | Record compiler warnings. | Latest full solution build is warning-free. |

Current validation snapshot:

- Unit-only test filter passes: 293/293 with `Category=Unit`.
- SQL integration test filter passes with Docker/Testcontainers available: 78 passed, 1 skipped, 79 total with `Category=Integration`.
- Full solution build completes with 0 warnings.
- CI workflow is defined in `.github/workflows/ci.yml` with separate build/unit and SQL integration jobs.

## Phase 1: Core Correctness

Goal: restore the fundamental queue guarantees before adding new product capabilities.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 1.1 | P0 | Done | Introduce `SqlChokaQQueue`. | SQL mode enqueue writes only to SQL storage and never depends on `InMemoryQueue` channel. |
| 1.2 | P0 | Done | Fix SQL DI replacement in `UseSqlServer()`. | SQL mode registers SQL storage, SQL queue, SQL worker manager, and SQL hosted worker predictably. |
| 1.3 | P0 | Done | Rewrite Hot to Archive transition atomically. | Success finalization cannot leave a job in both Hot and Archive or in neither table. |
| 1.4 | P0 | Done | Rewrite Hot to DLQ transition atomically. | Failure, cancellation, timeout, and zombie finalization cannot duplicate or lose jobs. |
| 1.5 | P0 | Done | Rewrite DLQ to Hot resurrection atomically. | Concurrent resurrection cannot create duplicates or leave stale DLQ rows. |
| 1.6 | P0 | Done | Fix `SqlJobWorker.ExecuteAsync` nested task issue. | Host observes fetcher and processor loops and shuts them down correctly. |
| 1.7 | P0 | Done | Track spawned processing tasks. | Shutdown waits for active jobs or intentionally releases them with documented semantics. |
| 1.8 | P1 | Done | Fix in-memory Bus mode job type key consistency. | In-memory and SQL modes both process jobs using the registered profile key. |

Notes:

- SQL mode now uses transaction-scoped `DELETE ... OUTPUT` buffers for Hot to Archive, Hot to DLQ, DLQ to Hot, zombie rescue, and batch cancellation moves.
- `SqlJobWorker` now awaits real fetcher/processor loop tasks, completes the prefetch buffer on shutdown, releases prefetched jobs that never started, and waits for active processing tasks to finish their cancellation/finalization path.
- SQL mode now replaces `IChokaQQueue` with `SqlChokaQQueue` and removes the in-memory producer/hosted worker path.
- In-memory Bus mode now uses `JobTypeRegistry` for execution and DLQ requeue paths, so custom profile keys behave the same as SQL mode.

## Phase 2: Worker Ownership Safety

Goal: eliminate stale worker finalization and zombie recovery races.

This phase incorporates the original "Phase C: Worker Ownership Safety" from the Operational Hardening Roadmap.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 2.1 | P0 | Done | Propagate `workerId` through `JobProcessor`. | Every final state transition knows which worker is attempting it. |
| 2.2 | P0 | Done | Update `IJobStateManager` contract with optional ownership. | State manager methods can enforce worker ownership. |
| 2.3 | P0 | Done | Update `IJobStorage` contract with optional `workerId`. | Storage methods can guard finalization by current owner. |
| 2.4 | P0 | Done | Add SQL ownership guards. | `ArchiveSucceeded`, `RescheduleForRetry`, and `MoveToDLQ` include worker ownership checks. |
| 2.5 | P0 | Done | Detect zero-row finalization. | If a stale worker finalizes 0 rows, code logs and does not emit false success/failure events. |
| 2.6 | P1 | Done | Add stale worker race integration tests. | Frozen worker wake-up after zombie rescue cannot archive/retry a reclaimed job. |

Target SQL guard shape:

```sql
WHERE [Id] = @Id
  AND ([WorkerId] = @WorkerId OR @WorkerId IS NULL)
```

Design note:

- `@WorkerId IS NULL` should be reserved for explicit administrative operations, not normal worker finalization.
- Worker-owned finalization now returns `false` on zero-row moves, allowing the state manager to suppress false UI notifications.

## Phase 3: Lease, Prefetch, And Bulkhead Correctness

Goal: make concurrency semantics predictable under load and across multiple instances.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 3.1 | P0 | Done | Introduce lease token or fetch token. | A buffered job cannot start processing after its lease has been released or reclaimed. |
| 3.2 | P1 | Done | Separate fetched timeout from processing timeout. | Prefetched jobs are not reclaimed prematurely by zombie recovery. |
| 3.3 | P1 | Done | Fix per-queue `MaxWorkers` batch overshoot. | A single fetch batch cannot exceed a queue's `MaxWorkers` limit. |
| 3.4 | P1 | Done | Define attempt count semantics. | Attempt count changes exactly once per actual execution attempt, not per temporary buffer movement. |
| 3.5 | P1 | Done | Add pause-during-prefetch tests. | Paused queues release jobs without later stale processing. |
| 3.6 | P1 | Done | Add multi-instance SQL fetch tests. | Multiple workers cannot process the same job concurrently. |

Suggested SQL strategy for bulkhead:

- Use `ROW_NUMBER() OVER (PARTITION BY Queue ORDER BY Priority DESC, ScheduledAtUtc, CreatedAtUtc)`.
- Limit selected rows per queue by remaining capacity.

Completed notes:

- `MarkAsProcessingAsync` is now an ownership-aware execution gate.
- `JobProcessor` skips dispatch when a prefetched job no longer owns its persisted lease.
- SQL and in-memory storage return a boolean result so stale buffered copies become safe no-ops.
- `FetchedJobTimeoutSeconds` now controls abandoned fetched-job recovery separately from `ZombieTimeoutSeconds`.
- SQL fetch uses per-queue `ROW_NUMBER()` caps so one batch cannot claim more than remaining `MaxWorkers`.
- `AttemptCount` now advances at `MarkAsProcessing`, not during fetch, release, or abandoned recovery.
- SQL worker refreshes queue pause state before execution and releases buffered jobs when operators pause a queue.
- Concurrent SQL fetch integration coverage proves workers do not receive duplicate job claims.

## Phase 4: Security Hardening

Goal: make The Deck safe as an administrative control plane.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 4.1 | P1 | Done | Make dashboard secure by default. | Public dashboard requires explicit opt-in, for example `AllowAnonymousDeck = true`. |
| 4.2 | P1 | Done | Validate Hub command inputs. | Null, empty, oversized, and malformed job IDs, queue names, payloads, and batches are rejected. |
| 4.3 | P1 | Done | Use authenticated actor for audit fields. | Edits, cancels, resurrects, and purges record the real user identity. |
| 4.4 | P1 | Done | Split read and destructive policies. | Users can be granted read-only dashboard access without purge/edit/retry privileges. |
| 4.5 | P1 | Done | Remove secrets from samples. | Sample appsettings do not contain real-looking SQL passwords. |
| 4.6 | P2 | Done | Add security docs. | Host apps have clear setup examples for authentication and authorization. |

Current state:

- Authorization policy exists and `AllowAnonymousDeck` must be explicitly set for public access.
- If no named policy is configured, The Deck and Hub use the host app's default authorization policy.
- Hub commands reject null, empty, oversized, malformed payload, and unsafe batch inputs before storage calls.
- `DestructiveAuthorizationPolicy` gates cancel, retry/resurrect, edit, purge, pause, deactivate, and queue-limit commands separately from read access.
- Hub commands use the authenticated SignalR user identity for operator audit fields, with fallback only for tests or explicit anonymous demos.
- Sample appsettings no longer contain real-looking SQL passwords.
- Destructive commands include cancel, retry/resurrect, edit, purge, queue pause, queue deactivate.

## Phase 5: Observability Core

Goal: provide a single-pane-of-glass view for saturation and health.

This phase incorporates the original "Phase A: Observability Core (Dashboard)" from the Operational Hardening Roadmap.

### 5.1 Queue Lag

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 5.1.1 | P1 | Done | Keep recording execution-time queue lag metric. | `RecordQueueLag` remains emitted when jobs are processed. |
| 5.1.2 | P1 | Done | Add `GetSystemHealthAsync` to `IJobStorage`. | Storage returns per-queue Avg/Max pending lag without expensive table scans. |
| 5.1.3 | P1 | Done | Implement SQL health CTE. | Query calculates lag from pending jobs using indexed access paths. |
| 5.1.4 | P1 | Done | Add dashboard queue lag indicators. | Per-queue status is visible in The Deck. |
| 5.1.5 | P2 | Done | Add lag thresholds configuration. | Defaults are healthy under 5s, warning 5-10s, critical over 10s. |

Default UI thresholds:

| Lag | State |
|---|---|
| Under 5 seconds | Healthy |
| 5 to 10 seconds | Warning |
| Over 10 seconds | Critical |

### 5.2 Throughput And Failure Rate

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 5.2.1 | P1 | Done | Track jobs processed per second for last 1 minute. | Dashboard shows rolling 1m throughput. |
| 5.2.2 | P1 | Done | Track jobs processed per second for last 5 minutes. | Dashboard shows rolling 5m throughput. |
| 5.2.3 | P1 | Done | Track failure percentage. | Dashboard shows real-time failed vs succeeded percentage. |
| 5.2.4 | P2 | Done | Add rolling event storage strategy. | Throughput and failure-rate windows read transactional `MetricBuckets` instead of scanning recent Archive/DLQ rows. |

### 5.3 DLQ Breakdown

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 5.3.1 | P1 | Done | Add top DLQ error query. | Storage groups failures by reason and normalized error prefix. |
| 5.3.2 | P1 | Done | Add "Top Error Types" DTO. | UI receives top 5 error groups with counts and latest timestamp. |
| 5.3.3 | P1 | Done | Add dashboard widget. | Operators can see why jobs are failing at a glance. |
| 5.3.4 | P2 | Done | Add click-through filter from top errors. | Selecting a top error switches to DLQ and applies the matching `FailureReason` plus error-prefix search. |

## Phase 6: Failure Transparency

Goal: make error triage fast for operators.

This phase incorporates the original "Phase B: Failure Transparency (UI Badges)" from the Operational Hardening Roadmap.

### 6.1 Failure Taxonomy

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 6.1.1 | P1 | Done | Preserve existing failure reasons. | Existing DLQ rows remain readable. |
| 6.1.2 | P1 | Done | Add `Throttled` failure reason. | Downstream rate-limit failures are visible in DLQ taxonomy. |
| 6.1.3 | P1 | Done | Add `FatalError` failure reason. | Poison pills that bypass retries are categorized explicitly. |
| 6.1.4 | P1 | Done | Add `Timeout` failure reason. | Execution timeout is not conflated with admin cancellation. |
| 6.1.5 | P1 | Done | Add `Transient` or `RetriesExhaustedTransient` reason. | Standard retry exhaustion is distinguishable from fatal failure. |
| 6.1.6 | P1 | Done | Map `JobProcessor` exception taxonomy into DLQ reason. | Fatal, throttled, timeout, and transient paths store the correct reason. |

Current enum:

- `MaxRetriesExceeded`
- `Cancelled`
- `Zombie`
- `CircuitBreakerOpen`
- `Rejected`

Target additions:

- `Throttled`
- `FatalError`
- `Timeout`
- `Transient`

### 6.2 Visual Badging

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 6.2.1 | P1 | Done | Add `FailureReason` to `JobViewModel`. | DLQ rows carry taxonomy data to UI components. |
| 6.2.2 | P1 | Done | Add failure badges to `JobRow`. | DLQ rows show color-coded failure reason badges. |
| 6.2.3 | P1 | Done | Add DLQ reason filter to history filter. | Operators can filter DLQ by taxonomy labels. |
| 6.2.4 | P2 | Done | Add error prefix/exception type display. | Top Error Types displays normalized prefixes; DLQ rows show a compact exception-family prefix with full details available as tooltip/inspector data. |

## Phase 7: Operational Controls

Goal: enable safe management at scale.

This phase incorporates the original "Phase D: Operational Controls (Bulk Actions)" from the Operational Hardening Roadmap.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 7.1 | P1 | Done | Bulk requeue selected DLQ jobs. | Selected-row retry/resurrect is available and guarded by explicit confirmation. |
| 7.2 | P1 | Done | Subset purge by failure reason. | Operators can purge DLQ rows matching `FailureReason` through bounded filtered bulk operations. |
| 7.3 | P1 | Done | Subset purge by job type. | Operators can purge DLQ rows matching exact job type through bounded filtered bulk operations. |
| 7.4 | P1 | Done | Subset requeue by failure reason/type. | Operators can requeue matching failed jobs without manually selecting every row. |
| 7.5 | P1 | Done | Payload edit before requeue. | DLQ editor can repair payload/priority and requeue the job through one atomic operator command. |
| 7.6 | P2 | Done | Add dry-run previews for bulk destructive operations. | UI shows matched count, affected count, cap, and sample IDs before purge/requeue. |
| 7.7 | P2 | Done | Add max batch limits and confirmation. | Filtered bulk operations are capped and typed; selected-row bulk actions require explicit confirmation. |

Current state:

- Bulk cancel exists.
- Bulk retry/resurrect exists for selected jobs.
- Bulk purge exists for selected DLQ jobs.
- Filtered DLQ purge/requeue by queue, failure reason, job type, date range, and search term exists.
- Filtered bulk operations have dry-run previews, hard caps, and typed confirmation.
- Selected-row bulk cancel/retry/purge actions require an explicit confirmation step.
- Single-job payload edit exists.
- Guided edit-and-requeue exists for DLQ payload repair.

Latest validation snapshot:

- Unit-only test filter passes: 293/293 with `Category=Unit`.
- SQL integration test filter passes with Docker/Testcontainers available: 78 passed, 1 skipped, 79 total with `Category=Integration`.

## Phase 8: Public API And Semantics

Goal: align public APIs with documented guarantees.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 8.1 | P1 | Done | Expose idempotency key through enqueue path. | `IIdempotentJob.IdempotencyKey` and explicit `idempotencyKey:` enqueue option reach storage. |
| 8.2 | P1 | Done | Define idempotency scope. | Docs and tests define enqueue dedupe as Hot-only; Archive/DLQ do not block future attempts. |
| 8.3 | P2 | Done | Add delayed enqueue to `IChokaQQueue`. | Users can schedule jobs with `delay:` without direct storage calls. |
| 8.4 | P1 | Done | Make execution timeout configurable. | Processor timeout is driven by `ChokaQOptions.Execution.DefaultTimeout`, not a hardcoded 15-minute value. |
| 8.5 | P2 | Done | Add per-job or per-queue execution timeout extension point. | Per-queue `Queues:{name}:ExecutionTimeout` lets long-running and short-running workloads coexist safely. |

Current state:

- Public `IChokaQQueue.EnqueueAsync` supports named `delay:` and `idempotencyKey:` options.
- Jobs implementing `IIdempotentJob` automatically provide the enqueue idempotency key unless an explicit key is supplied.
- Built-in enqueue dedupe is scoped to active Hot jobs only.
- Result idempotency after completion remains the responsibility of the optional result-idempotency middleware.
- Runtime policy can be bound from `IConfiguration` through `AddChokaQ(configurationSection, configure)` and `UseSqlServer(configurationSection)`.
- Execution timeout, retry attempts, exponential backoff, jitter, circuit-breaker delay, heartbeat intervals, zombie scan interval, in-memory pause delay, SQL cleanup batch size, and SQL polling/transient retry policy are configurable.
- Configuration is validated at startup so unsafe values fail before workers claim jobs.

Latest validation snapshot:

- Unit-only test filter passes: 293/293 with `Category=Unit`.
- SQL integration test filter passes with Docker/Testcontainers available: 78 passed, 1 skipped, 79 total with `Category=Integration`.

## Phase 9: Data Model And SQL Maturity

Goal: make SQL Server behavior safe at production data volumes.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 9.1 | P1 | Done | Add schema version table. | `SchemaMigrations` records applied ChokaQ SQL schema versions 1-3 and remains idempotent across startup provisioning. |
| 9.2 | P1 | Done | Add command timeout configuration. | `SqlServer.CommandTimeoutSeconds` applies to storage, dashboard, recovery, admin, and schema initialization commands. |
| 9.3 | P1 | Done | Review fetch/archive/DLQ indexes. | Fetch, active dashboard, recovery, DLQ type/date, and bounded bulk-operation paths have dedicated indexed access paths. |
| 9.4 | P1 | Done | Batch archive and DLQ purge operations. | Archive purge and selected DLQ purge run through `CleanupBatchSize`-bounded transactions and update counters by rows actually deleted. |
| 9.5 | P2 | Done | Review `NOLOCK` usage. | Dirty reads are removed from fetch, operator preview, history, inspector, and queue-management reads; remaining `NOLOCK` use is documented as dashboard telemetry only. |
| 9.6 | P2 | Done | Add SQL query performance tests. | Fetch, system-health, and history-paging SQL paths have real SQL Server baseline tests with explicit wall-clock budgets. |

Current state:

- `SchemaMigrations` provides a database-side migration ledger for incident response and future upgrades.
- Version 3 records the `MetricBuckets` rolling-observability schema upgrade.
- SQL command timeout is configurable through `SqlJobStorageOptions.CommandTimeoutSeconds` and `ChokaQ:SqlServer:CommandTimeoutSeconds`.
- The command timeout is applied centrally by `SqlMapper` to every command created for a ChokaQ SQL connection.
- Fetch index now includes `CreatedAtUtc` to match the effective scheduling sort used by SQL workers.
- Hot-table recovery scans use filtered Fetched/Processing indexes, and recovery predicates avoid wrapping timestamp columns in `DATEDIFF`.
- DLQ operator workflows now have type and original-created-date indexes in addition to queue, reason, and failed-date indexes.
- SQL cleanup batch size is configurable through `SqlJobStorageOptions.CleanupBatchSize` and `ChokaQ:SqlServer:CleanupBatchSize`.
- Archive purge and selected DLQ purge now repeat short SQL transactions instead of issuing one large delete, which bounds lock duration and transaction-log pressure.
- `NOLOCK` is now restricted to passive dashboard telemetry: summary counters, queue health, throughput, and top-error grouping.
- Fetch bulkhead decisions, DLQ bulk previews, job inspectors, history pages, and queue-management reads use committed reads.
- Unit tests guard the read-consistency policy so future `NOLOCK` additions outside telemetry fail fast.
- SQL query performance baselines seed large test datasets directly in SQL and verify fetch, system-health, archive paging, and DLQ paging stay inside explicit budgets.
- The performance tests are guardrails for query-shape regressions, not microbenchmarks; production hosts should still use Query Store and database telemetry for real sizing.

Latest validation snapshot:

- Unit-only test filter passes: 293/293 with `Category=Unit`.
- SQL integration test filter passes with Docker/Testcontainers available: 78 passed, 1 skipped, 79 total with `Category=Integration`.

## Phase 10: Operability And Runtime Management

Goal: make ChokaQ easy to run, alert on, and debug.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 10.1 | P1 | Done | Add health checks. | Host app can expose SQL connectivity, worker liveness, and queue saturation checks through ASP.NET Core health checks. |
| 10.2 | P1 | Done | Keep standard metrics. | OpenTelemetry instrument names, units, and expected tags are covered by contract tests and docs. |
| 10.3 | P1 | Done | Control metric cardinality. | Queue/type/error/reason labels are capped by `ChokaQ:Metrics` and overflow into a stable bucket. |
| 10.4 | P2 | Done | Add structured log event IDs. | Lifecycle events have stable `EventId` values for SIEM queries, alerts, and runbook reconstruction. |
| 10.5 | P2 | Done | Add dashboard consistency pass. | The Deck reconciles counters, health, circuit state, and the active view from storage after operator mutations. |
| 10.6 | P2 | Done | Document backpressure policy. | SQL and in-memory enqueue/backlog behavior is documented, configurable, and linked from the docs site. |

Current state:

- `AddChokaQHealthChecks` registers generic worker liveness and queue saturation checks for in-memory, bus, and pipe hosts.
- `AddChokaQSqlServerHealthChecks` composes the generic checks with SQL connectivity and schema readiness.
- Health thresholds are configurable through `ChokaQ:Health`, with defaults aligned to The Deck queue-lag colors.
- Samples and docs map `/health` using standard ASP.NET Core health-check primitives.
- Worker managers now expose process-local `IsRunning` and `LastHeartbeatUtc` signals for readiness without conflating them with per-job zombie heartbeats.
- The `ChokaQ` OpenTelemetry meter publishes eight documented instruments for enqueue, success, failure, duration, queue lag, DLQ, retry, and active workers.
- Metric contract tests verify instrument names, `ms` units, and required tags.
- `ChokaQ:Metrics` caps distinct `queue`, `type`, `error`, and `reason` tag values per process and collapses overflow to `other`.
- Blank metric tag values become `unknown`; oversized tag values are trimmed before cardinality tracking.
- `ChokaQLogEvents` defines stable event ranges for worker lifecycle, job execution, state transitions, enqueue, recovery, SQL provisioning, and admin commands.
- Critical runtime logs now include `EventId` values for success archive, stale lease rejection, retries, DLQ moves, zombie rescue, SQL initialization, enqueue duplicates, and worker loop failures.
- Unit tests enforce unique EventId values and pin the published high-value IDs.
- The Deck no longer applies optimistic local decrements to failed counters after retry/purge; storage is the canonical source after every operator mutation.
- History and DLQ pages refresh their shell state together with the active table slice, so counters, health, circuits, and rows stay aligned.
- Bulk purge/requeue now clamps history pagination to the new last page when the current page becomes empty after mutation.
- Live SignalR updates respect the active Hot status filter and remove rows that move out of the selected status.
- Dashboard paging normalization is covered by unit tests.
- Backpressure policy is documented as a production contract: SQL mode uses `JobsHot` as the durable backlog, while the SQL prefetch channel stays bounded and local.
- In-memory mode now has an appsettings-friendly `ChokaQ:InMemory:MaxCapacity` alias, startup validation, and docs that describe its soft history-retention cap.
- The docs site includes a dedicated Backpressure Policy page linked from Deep Dives, configuration, README, and in-memory engine docs.

## Phase 11: Packaging And Release Readiness

Goal: prepare the project for external adoption.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 11.1 | P2 | Done | Decide target frameworks. | ChokaQ targets `net10.0` only for the current preview line; older TFMs are explicitly unsupported. |
| 11.2 | P2 | Done | Decide package shape. | Future publishing starts as one `ChokaQ` package; storage-provider split is deferred until multiple providers exist. |
| 11.3 | P2 | Done | Document NuGet readiness strategy without publishing. | Release strategy explains why actual NuGet pack/publish is deferred while APIs and The Deck continue moving. |
| 11.4 | P2 | Done | Add sample Docker Compose. | New users can run the SQL Bus sample, SQL Server, The Deck, and health checks with one documented command. |
| 11.5 | P2 | Done | Remove aspirational docs or mark them as planned. | README and docs describe current source/Docker usage, no published NuGet, minimal dependency reality, and planned product direction clearly. |
| 11.6 | P2 | Done | Add release checklist. | `docs/release-checklist.md` defines build, unit test, SQL integration, docs, Docker smoke, security, database, and frozen future-package gates. |

Current release strategy:

- No NuGet package is published yet.
- The current development line targets `net10.0` only to avoid premature multi-target compatibility work.
- The intended first public package is a single `ChokaQ` package containing abstractions, core, SQL Server storage, The Deck, health checks, configuration, and docs.
- Separate storage packages are deferred until there is more than one production storage provider.
- Actual package metadata, pack, and publish work is deferred until the dashboard/API surface is more stable.
- Root `docker-compose.yml` starts SQL Server 2022 and the Bus sample; `docs/samples/docker-compose.md` documents launcher, Deck, health, SQL port, password override, and reset commands.
- Release decisions are recorded in `docs/release-strategy.md`.
- Public docs now distinguish implemented behavior from product direction: ChokaQ is active development, not a published NuGet package yet, and SQL Server mode uses the official `Microsoft.Data.SqlClient` driver while avoiding EF/Dapper/Polly-style infrastructure dependencies.
- `docs/release-checklist.md` is the operational release gate for source truth, build, unit tests, SQL integration, docs, Docker Compose smoke, database readiness, security/operations, and future NuGet work.

## Phase 12: Documentation Site Depth

Goal: make the docs site the single comprehensive documentation surface for product usage, architecture, operations, and system-design study.

Execution note: the separate long-form content layer is closed. Useful outline material now belongs directly in the docs site through the Architecture Study Guide and the existing deep-dive pages.

| ID | Priority | Status | Work Item | Acceptance Criteria |
|---|---|---|---|---|
| 12.1 | P2 | Done | Add architecture study guide entry point. | `docs/study-guide.md` defines the reading model, site page template, study map, and interview framing. |
| 12.2 | P2 | Done | Fold long-form outline material into the site. | The study guide contains a site expansion map that routes useful architecture topics to existing docs pages. |
| 12.3 | P2 | Done | Keep deep explanations site-owned. | Future code walkthroughs, operational notes, and system-design framing are documented as docs-site expansion work. |
| 12.4 | P2 | Done | Remove separate content-layer artifacts. | No source docs require a separate non-site documentation layer. |

Documentation standard:

- Explain what was built and why it exists.
- Prefer concrete failure modes over abstract pattern names.
- Repeat important ideas in setup docs, code comments, deep dives, and runbooks when that helps readers find the concept.
- Write for capable technical readers: explicit, respectful, and operationally honest.

## Original Operational Hardening Roadmap Mapping

| Original Phase | Mega-Roadmap Location | Current Status |
|---|---|---|
| Phase A: Observability Core | Phase 5 | Done for current scope. Queue lag, throughput, failure rate, top errors, and Top Errors click-through are implemented. |
| Phase B: Failure Transparency | Phase 6 | Done for current scope. DLQ rows show taxonomy badges and exception-family prefixes. |
| Phase C: Worker Ownership Safety | Phase 2 | Done. Worker-owned finalization is guarded and tested. |
| Phase D: Operational Controls | Phase 7 | Done for current scope. Filtered bulk controls, guided edit-and-requeue, caps, previews, and confirmations are implemented. |

## Known Issue Register

| ID | Status | Issue | Next Action |
|---|---|---|---|
| None | Done | No active known roadmap issue is listed. | Keep release and docs checks current as the site expands. |

## Current Highest-Risk Open Items

No active open items are listed in this roadmap.

## Production Preview Definition Of Done

ChokaQ can be called production-preview ready when:

- No P0 items remain open.
- Unit tests pass locally and in CI.
- SQL integration tests pass in CI.
- SQL mode enqueue no longer depends on in-memory channel.
- All state transitions are atomic and ownership-aware.
- Worker shutdown is graceful and observable.
- The Deck is not public unless explicitly configured.
- Queue lag, failure rate, and top DLQ errors are visible in dashboard.
- Failure taxonomy is persisted and filterable.
- README accurately describes implemented behavior.
