# ChokaQ Feature Inventory

This file is an internal, single-page inventory of ChokaQ capabilities and
release-readiness signals. It is intentionally not linked from the public docs
navigation.

## Current Product Status

ChokaQ is in **active development / production-preview hardening**.

- The project targets `.NET 10`.
- ChokaQ is not published as a NuGet package yet.
- Current usage is through source project references or the Docker Compose
  sample.
- The roadmap lists no active open items.
- The current posture is production-preview candidate, not GA/stable release.

## Production-Preview Definition Of Done

| Requirement | Status | Notes |
|---|---|---|
| No P0 items remain open. | Done | Roadmap has no active P0/P1 blockers listed. |
| Unit tests pass locally and in CI. | Done | Latest local unit-style run: 293 passed, 0 failed. |
| SQL integration tests pass in CI. | Done in roadmap snapshot | Roadmap snapshot: 78 passed, 1 skipped with SQL/Testcontainers available. |
| SQL mode enqueue no longer depends on in-memory channel. | Done | SQL mode uses `SqlChokaQQueue` and SQL storage directly. |
| All state transitions are atomic and ownership-aware. | Done | Hot/Archive/DLQ transitions use transaction-scoped SQL moves with ownership guards. |
| Worker shutdown is graceful and observable. | Done | SQL worker tracks fetcher, processor, prefetched jobs, and active processing tasks. |
| The Deck is not public unless explicitly configured. | Done | Dashboard authorization is secure by default; anonymous access requires explicit opt-in. |
| Queue lag, failure rate, and top DLQ errors are visible in dashboard. | Done | Rolling observability uses `MetricBuckets` and dashboard widgets. |
| Failure taxonomy is persisted and filterable. | Done | DLQ rows carry failure reason data and UI filters/badges. |
| README accurately describes implemented behavior. | Done | README states active development, no published NuGet, current source/Docker usage. |

## Runtime And Integration Model

- **2x2 architecture:** Bus or Pipe processing over either In-Memory or SQL
  Server storage.
- **Bus mode:** strongly typed jobs, dedicated handlers, dependency-injection
  scopes, and job profiles.
- **Pipe mode:** high-throughput processing of raw payloads through a single
  global handler.
- **In-memory mode:** zero-config RAM storage based on
  `System.Threading.Channels`, intended for local/dev or volatile workloads.
- **SQL Server mode:** durable storage through the official
  `Microsoft.Data.SqlClient` driver and project-owned SQL infrastructure code.
- **Source integration:** host apps reference the ChokaQ projects directly until
  package publishing is ready.
- **Docker Compose sample:** repository sample starts SQL Server, a Bus sample,
  The Deck, and health checks.

## Core Engine

- **Minimal dependency footprint:** no EF Core, no Dapper, no Polly, no mediator
  dependency for core infrastructure behavior.
- **Custom SQL mapping:** lightweight project-owned `SqlMapper` and mapping
  code for explicit SQL control.
- **Expression Tree dispatch:** handlers are invoked through cached compiled
  delegates instead of reflection-based `MethodInfo.Invoke`.
- **Middleware pipeline:** `IChokaQMiddleware` lets hosts add cross-cutting
  behavior such as logging, validation, or correlation.
- **Runtime configuration binding:** execution, retry, recovery, in-memory, SQL,
  health, and metric options can be bound from `IConfiguration`.
- **Startup validation:** invalid operational values fail during startup before
  workers claim production jobs.

## Data Architecture

- **Three Pillars layout:** physically separates `JobsHot`, `JobsArchive`, and
  `JobsDLQ`.
- **Hot table:** active `Pending`, `Fetched`, and `Processing` work only.
- **Archive table:** successful history with read-oriented access patterns.
- **DLQ table:** failed, canceled, and zombie jobs for inspection and operator
  repair.
- **Single-table avoidance:** active-work indexes stay small instead of being
  mixed with historical rows.
- **SQL auto-provisioning:** optional startup schema creation for runtime
  tables, summary tables, metric buckets, queue metadata, and indexes.
- **Schema migration ledger:** `SchemaMigrations` records applied ChokaQ schema
  versions.
- **Metric buckets schema:** versioned SQL schema supports rolling throughput and
  failure-rate windows.
- **Hot-path indexes:** fetch, recovery, dashboard, history, DLQ, and bulk
  operation paths have dedicated indexed access paths.
- **PAGE compression for history:** archive-oriented storage can use SQL Server
  compression where configured by schema.
- **Batched cleanup:** archive and DLQ cleanup work is bounded by configurable
  batch size.
- **Command timeout policy:** SQL command timeout is configurable and applied
  across storage, dashboard, recovery, admin, and schema operations.
- **NOLOCK policy:** dirty reads are removed from operator and correctness paths;
  remaining use is limited to passive dashboard telemetry.

## SQL Enqueue And Public API Semantics

- **Dedicated SQL queue:** `SqlChokaQQueue` writes durable jobs directly to SQL
  storage.
- **Correct SQL DI replacement:** `UseSqlServer()` registers SQL storage, SQL
  queue, SQL worker manager, and hosted SQL worker predictably.
- **Delayed enqueue:** `IChokaQQueue.EnqueueAsync` supports delayed scheduling.
- **Explicit idempotency keys:** callers can provide an `idempotencyKey`.
- **Job-provided idempotency:** jobs implementing `IIdempotentJob` can supply
  their own idempotency key automatically.
- **Hot-only enqueue dedupe:** built-in deduplication applies to active Hot jobs;
  Archive and DLQ do not block future attempts.
- **Result idempotency boundary:** completed-work idempotency remains the
  responsibility of optional middleware or application logic.
- **Consistent job type keys:** in-memory and SQL Bus paths both use registered
  profile keys for execution and requeue.

## State Transitions And Worker Lifecycle

- **Atomic Hot to Archive:** successful finalization cannot duplicate or lose a
  job between Hot and Archive.
- **Atomic Hot to DLQ:** failure, cancellation, timeout, and zombie finalization
  cannot duplicate or lose a job.
- **Atomic DLQ to Hot:** resurrection/requeue avoids duplicates and stale DLQ
  rows under concurrency.
- **Transaction-scoped SQL movement:** SQL mode uses `DELETE ... OUTPUT` style
  transitions for table moves.
- **Ownership-aware finalization:** archive, retry, and DLQ transitions can
  require the current worker owner.
- **Zero-row finalization detection:** stale workers do not emit false success or
  failure notifications.
- **Worker ID propagation:** processing and state-manager paths carry worker
  identity where ownership matters.
- **Graceful shutdown:** hosted worker shutdown observes fetcher and processor
  loops, completes prefetch buffers, releases unstarted jobs, and waits for
  active tasks to finish cancellation/finalization.
- **Prefetch release:** jobs fetched but never started can be released safely.
- **Execution gate:** `MarkAsProcessingAsync` verifies persisted ownership before
  user handler dispatch.
- **Attempt-count semantics:** attempts advance once per real execution attempt,
  not during temporary fetch/release movement.
- **Pause-aware processing:** SQL workers refresh queue pause state before
  execution and release buffered jobs for paused queues.

## Concurrency And Backpressure

- **SQL competing consumers:** workers claim jobs with SQL locking semantics such
  as `UPDLOCK` and `READPAST`.
- **Multi-instance safety:** integration coverage verifies multiple workers do
  not process the same SQL job concurrently.
- **Lease/fetch ownership checks:** buffered jobs cannot start after their lease
  has been released or reclaimed.
- **Separate fetched timeout:** abandoned fetched jobs have a timeout distinct
  from processing zombie timeout.
- **Database-level bulkhead:** per-queue `MaxWorkers` limits are enforced through
  SQL fetch logic for cluster-wide coordination.
- **Batch overshoot prevention:** SQL fetch ranks candidates per queue and caps
  selected rows by remaining queue capacity.
- **Dynamic concurrency limiter:** active worker capacity can change at runtime
  without restarting the host.
- **Lock-free runtime scaling design:** dynamic concurrency avoids mutable permit
  drift by using atomic state and coalesced wake signals.
- **Bounded SQL prefetch:** SQL workers use bounded local buffering to decouple
  database latency from processing throughput.
- **SQL backlog policy:** SQL mode treats `JobsHot` as the durable backlog.
- **In-memory backpressure:** in-memory mode uses bounded channels and
  `ChokaQ:InMemory:MaxCapacity`.

## Reliability And Recovery

- **Smart Worker failure routing:** fatal and transient failures follow different
  paths.
- **Fatal exception fast-fail:** poison/code-defect failures can bypass retries
  and go straight to DLQ.
- **Transient retries:** retry attempts use configurable exponential backoff and
  jitter.
- **Circuit breaker:** per-job-type in-memory circuit state can block repeated
  failing work and reschedule pressure.
- **Timeout handling:** execution timeout is configurable globally and per queue.
- **Cancellation isolation:** business cancellation is separated from
  infrastructure finalization semantics.
- **ZombieRescueService:** background recovery handles abandoned fetched jobs and
  expired processing heartbeats.
- **Fetched recovery:** jobs stuck in `Fetched` can return to `Pending`.
- **Processing zombie handling:** jobs stuck in `Processing` with expired
  heartbeat move to DLQ as zombies.
- **Worker session identity:** worker ownership uses unique worker/session IDs so
  the cluster can distinguish replicas.
- **SQL transient retry policy:** storage operations can retry transient SQL
  failures separately from user handler retry behavior.

## Failure Taxonomy And DLQ

- **Persisted failure reasons:** DLQ rows store structured failure taxonomy.
- **Supported reasons include:** `MaxRetriesExceeded`, `Cancelled`, `Zombie`,
  `CircuitBreakerOpen`, `Rejected`, `Throttled`, `FatalError`, `Timeout`, and
  `Transient`.
- **Failure badges:** The Deck shows color-coded DLQ reason badges.
- **DLQ reason filter:** operators can filter failed jobs by taxonomy.
- **Error prefix display:** top errors and DLQ rows expose normalized exception
  families for triage.
- **Top error grouping:** storage groups failures by reason and normalized error
  prefix.
- **Click-through triage:** selecting top errors can apply matching DLQ filters.
- **Edit + Resurrect:** operators can repair payload/priority and requeue failed
  work through an atomic command.
- **Hot-edit safety:** active jobs are protected from unsafe mutation.

## The Deck Dashboard

- **Built-in control plane:** Blazor Server + SignalR dashboard hosted by the
  application.
- **Secure by default:** dashboard and Hub require authorization unless
  anonymous access is explicitly enabled.
- **Separate destructive authorization:** destructive commands can require a
  stricter policy than read-only access.
- **Authenticated audit fields:** operator actions use the authenticated SignalR
  user identity where available.
- **Input validation:** Hub commands reject null, empty, oversized, malformed, or
  unsafe inputs before storage calls.
- **Real-time updates:** SignalR pushes job state changes and dashboard updates.
- **Live active-job matrix:** virtualized active job grid for current work.
- **History views:** server-side filtering for Archive and DLQ history.
- **Inspector:** job details and exception stack traces are available for
  operator inspection.
- **Queue management:** queues can be paused, resumed, deactivated, and adjusted
  at runtime.
- **Runtime worker limits:** queue concurrency limits can be changed without
  restart.
- **Bulk actions:** selected jobs can be retried, canceled, purged, or resurrected
  with confirmation.
- **Filtered bulk operations:** DLQ purge/requeue can target queue, failure
  reason, job type, date range, and search criteria.
- **Dry-run previews:** filtered destructive operations show matched counts,
  affected counts, caps, and sample IDs before mutation.
- **Pagination consistency:** dashboard pages clamp after mutations when the
  current page becomes empty.
- **Canonical storage refresh:** The Deck reconciles counters and views from
  storage after operator mutations instead of trusting optimistic local changes.
- **Circuit view:** operators can inspect closed/open/half-open circuit states.
- **Console stream:** system-wide events and logs can stream into the dashboard
  console view.
- **Themes:** built-in Blueprint and Carbon visual themes.

## Observability And Operations

- **OpenTelemetry-compatible metrics:** ChokaQ emits instruments through
  `System.Diagnostics.Metrics` using meter name `ChokaQ`.
- **No exporter lock-in:** host apps choose Prometheus, OTLP, Application
  Insights, or another exporter.
- **Metric instruments cover:** enqueued jobs, succeeded jobs, failed jobs,
  execution duration, queue lag, DLQ moves, retries, and active workers.
- **Metric contract tests:** tests pin instrument names, units, and required
  tags.
- **Metric cardinality caps:** queue, type, error, and reason tags are capped and
  overflow to a stable configured value.
- **Unknown and oversized tag handling:** blank tags become `unknown`; long tags
  are trimmed before cardinality tracking.
- **Rolling observability:** throughput and failure-rate windows use transactional
  `MetricBuckets`.
- **Queue lag health:** storage calculates per-queue pending lag for dashboard and
  health use.
- **Health checks:** host apps can expose SQL connectivity, schema readiness,
  worker liveness, and queue saturation.
- **Health thresholds:** queue lag maps to Healthy/Degraded/Unhealthy using
  configurable thresholds.
- **Structured log event IDs:** stable event ranges exist for worker lifecycle,
  job execution, state transitions, enqueue, recovery, SQL provisioning, and
  admin commands.
- **High-value event IDs are pinned:** tests enforce uniqueness and important
  published IDs.
- **Backpressure documentation:** SQL and in-memory backlog behavior is documented
  as a production contract.

## Security And Authorization

- **Host-owned authentication:** ChokaQ uses ASP.NET Core authorization policies
  instead of shipping its own identity system.
- **Default authorization policy support:** if no named policy is supplied, The
  Deck uses the host application's default policy.
- **Read vs destructive policy split:** hosts can allow read access separately
  from cancel, retry, edit, purge, pause, deactivate, and limit changes.
- **Explicit anonymous demo mode:** anonymous dashboard access requires
  `AllowAnonymousDeck = true`.
- **Sample secrets removed:** sample appsettings avoid real-looking SQL
  credentials.

## Packaging, Release, And Documentation

- **No NuGet package yet:** package metadata, pack, and publish are deferred.
- **Single-package first plan:** future publishing starts as a single `ChokaQ`
  package unless provider split becomes necessary.
- **`net10.0` only for current preview line:** older target frameworks are
  explicitly unsupported for now.
- **Release strategy documented:** release readiness is tracked separately from
  implemented runtime behavior.
- **Release checklist documented:** build, unit, SQL integration, docs, Docker
  smoke, security, database, and future package gates are recorded.
- **Docs site depth:** the docs site is the comprehensive documentation surface
  for product usage, architecture, operations, and study notes.
- **Architecture Study Guide:** docs site includes a study map and expansion map
  for deeper architecture pages.

## Validation Snapshot

- Latest local full solution build: passed with 0 warnings and 0 errors.
- Latest local non-integration test run: 293 passed, 0 failed.
- Roadmap SQL integration snapshot: 78 passed, 1 skipped with
  Docker/Testcontainers available.
- Docs build was recently verified after the documentation restructure.
