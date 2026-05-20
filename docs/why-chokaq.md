# Why ChokaQ?

## The Problem

ChokaQ is designed for teams that want durable background processing without
turning the job system into a separate infrastructure platform. In practice, a
.NET team often has to choose how much persistence, observability, and operator
control it wants to own:

1. `Channel<T>` + `BackgroundService` is small and direct, but process-local.
   It needs additional work for durability, monitoring, retries, and recovery.
2. A larger job platform can provide more built-in capability, but it also
   brings more infrastructure, dependencies, and configuration surface.

ChokaQ sits in the middle: **SQL Server-backed reliability with in-process simplicity**.

## What ChokaQ Delivers

| Capability | ChokaQ |
|---------|--------|
| **Storage** | SQL Server (raw ADO.NET) — atomic, transactional |
| **Dependency Footprint** | Core uses Microsoft abstractions; SQL uses official `Microsoft.Data.SqlClient`; no general ORM, mapper, or resilience dependency |
| **Dashboard** | **The Deck** (Blazor Server + SignalR) — built-in |
| **ORM** | **Custom SqlMapper** — 172 lines, zero deps |
| **DLQ Management** | **Edit + Resurrect** — fix payloads in-browser |
| **Concurrency Control** | **DynamicConcurrencyLimiter** — dynamic runtime scaling |
| **Error Classification** | **Smart Worker** — fatal vs transient routing |
| **Bulkhead** | **Per-queue database-level** concurrency limits |
| **Circuit Breaker** | **Built-in per job type** — no external libraries |
| **Zombie Detection** | **ZombieRescueService** — automatic heartbeat monitoring |
| **Handler Invocation** | **Expression Trees** — cached compiled delegates |
| **Table Design** | **Three Pillars** — Hot/Archive/DLQ physical separation |

## The Single-Table Problem

A lifecycle store can keep pending, succeeded, and failed jobs in one physical
table. That is easy to start with, but active fetch and historical retention
pull the table in different directions. Over time:

- active indexes must coexist with long-lived historical rows;
- fetch queries spend more work filtering out rows that are not executable;
- cleanup and retention become part of hot-path performance management.

ChokaQ's **Three Pillars** architecture solves this by physically separating data:

| Pillar | Contains | Optimized For |
|--------|----------|---------------|
| **JobsHot** | Only Pending/Fetched/Processing | High-concurrency OLTP, `UPDLOCK + READPAST` |
| **JobsArchive** | Only Succeeded | Read-heavy analytics, PAGE compression |
| **JobsDLQ** | Only Failed/Cancelled/Zombie | Manual review, resurrection |

The Hot table stays focused on active work. Fetch queries remain much more
predictable than designs that mix pending and historical rows in one table.

## Minimal Dependency Philosophy

ChokaQ keeps dependencies intentionally small:

```
ChokaQ.Core
├── Microsoft.Extensions.Hosting.Abstractions
├── Microsoft.Extensions.DependencyInjection.Abstractions
└── Microsoft.Extensions.Logging.Abstractions

ChokaQ.Storage.SqlServer
└── Microsoft.Data.SqlClient (official Microsoft ADO.NET driver)
```

**What ChokaQ keeps inside the project boundary:**

| Area | ChokaQ implementation | Why |
|---------------|------------|-----|
| SQL mapping | `SqlMapper` + `TypeMapper` | Full control over parameter mapping and transitive dependencies |
| Storage resilience | `SqlRetryPolicy` + `InMemoryCircuitBreaker` | Policies tailored to ChokaQ storage and execution failures |
| SQL query shape | `Queries.cs` raw SQL templates | Precise control over locking hints, `OUTPUT` clauses, and CTEs |
| Handler dispatch | `BusJobDispatcher` + Expression Trees | Cached compiled delegates for typed handler invocation |

::: warning 🎯 Design Decision
Even small dependencies become part of the host application's compatibility
surface. ChokaQ keeps the SQL mapping layer narrow so the package can control
its query behavior and avoid unnecessary transitive version pressure.
:::

## Production Patterns Already Implemented

### 1. Atomic State Transitions
Moves between pillars use transaction-scoped SQL state transitions with ownership guards. No distributed transactions. No two-phase commit. If a transition is not applied, the worker observes that result instead of emitting a false success notification.

### 2. Self-Healing (ZombieRescueService)
A `BackgroundService` runs every 60 seconds:
- **Step 1:** Finds jobs stuck in `Fetched` state (worker crashed before processing) → resets to `Pending`
- **Step 2:** Finds jobs stuck in `Processing` with expired heartbeat → archives to DLQ as `Zombie`

### 3. Smart Error Handling
The Smart Worker distinguishes between:
- **Fatal errors** (`NullReferenceException`, `ArgumentException`, `JsonException`) → immediately to DLQ, no retries
- **Transient errors** (timeout, network blip) → exponential backoff with jitter

### 4. Observable by Default
Native OpenTelemetry via `System.Diagnostics.Metrics`:
```
chokaq.jobs.enqueued    (Counter)
chokaq.jobs.completed   (Counter)
chokaq.jobs.failed      (Counter)
chokaq.jobs.processing_duration (Histogram)
chokaq.jobs.queue_lag   (Histogram)
chokaq.jobs.dlq         (Counter)
chokaq.jobs.retried     (Counter)
chokaq.workers.active   (UpDownCounter)
```
No ChokaQ-specific exporter is required. Hosts listen to the `"ChokaQ"` meter through their normal OpenTelemetry setup. Metric tag cardinality is capped by `ChokaQ:Metrics`, so dynamic queue names, job types, errors, or failure reasons collapse into `other` after the configured budget.

Lifecycle logs use stable `EventId` values as well. Operators can query `JobRetriesExhaustedDlq` or `ZombieJobsArchived` directly instead of scraping text from log messages.

## When to Choose ChokaQ

ChokaQ is a good fit when the application already uses SQL Server and the team
wants durable background work with a small dependency footprint:

- SQL Server-centric environments;
- durable queue state without a separate broker for background work;
- real-time dashboard with operator recovery workflows;
- database-level queue isolation and bulkhead controls;
- environments that prefer a smaller transitive dependency surface;
- teams that want SQL query behavior and lifecycle transitions to be explicit.

**Not designed for:**

- event streaming systems where replayable logs and very high event volume are the primary requirement;
- cross-service pub/sub messaging patterns;
- PostgreSQL or Redis storage in the current preview line;
- recurring/scheduled jobs, which are not yet supported;
- multi-region distributed coordination.

## Architecture Notes

The documentation goes beyond setup snippets because background processing
systems fail in ways that are hard to debug after the fact. The design pages
explain the operational patterns ChokaQ relies on:

- at-least-once processing and idempotency;
- SQL competing consumers;
- worker ownership and lease checks;
- backpressure and bounded buffers;
- bulkhead isolation;
- circuit breakers;
- retry with exponential backoff and jitter;
- DLQ taxonomy and repair workflows;
- observability, health checks, and metric cardinality;
- secure administrative control planes.

<br>

> *Convinced? Jump to the [Getting Started](/getting-started) guide or dive into the [Three Pillars Architecture](/1-architecture/three-pillars).*
