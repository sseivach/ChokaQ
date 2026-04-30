# Why ChokaQ?

## The Problem

Most .NET background job solutions fall into two extremes:

1. **Too Simple:** `Channel<T>` + `BackgroundService` — volatile, no persistence, no monitoring, lost on crash
2. **Too Heavy:** Third-party frameworks with external dependencies, infrastructure overhead, complex configuration

ChokaQ sits in the middle: **SQL Server-backed reliability with in-process simplicity**.

## What ChokaQ Delivers

| Capability | ChokaQ |
|---------|--------|
| **Storage** | SQL Server (raw ADO.NET) — atomic, transactional |
| **Third-Party Dependencies** | **Zero** — only BCL packages |
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

Many background job frameworks store **all jobs** — pending, succeeded, failed — in one giant table. Over time:

- 📈 **Index fragmentation** degrades query performance
- 🐌 **Fetch queries slow down** because the engine scans millions of completed rows to find the few pending ones
- 💾 **Storage bloats** without manual cleanup scripts

ChokaQ's **Three Pillars** architecture solves this by physically separating data:

| Pillar | Contains | Optimized For |
|--------|----------|---------------|
| **JobsHot** | Only Pending/Fetched/Processing | High-concurrency OLTP, `UPDLOCK + READPAST` |
| **JobsArchive** | Only Succeeded | Read-heavy analytics, PAGE compression |
| **JobsDLQ** | Only Failed/Cancelled/Zombie | Manual review, resurrection |

The Hot table stays **tiny** — only active work. Fetch queries are always fast.

## Zero-Dependency Philosophy

ChokaQ's core depends on **nothing except Microsoft BCL packages**:

```
ChokaQ.Core
├── Microsoft.Extensions.Hosting.Abstractions
├── Microsoft.Extensions.DependencyInjection.Abstractions
└── Microsoft.Extensions.Logging.Abstractions

ChokaQ.Storage.SqlServer
└── Microsoft.Data.SqlClient (official Microsoft ADO.NET driver)
```

**What we built ourselves instead of importing:**

| Instead of... | We built... | Why |
|---------------|------------|-----|
| **Third-party ORMs** | `SqlMapper` + `TypeMapper` | Full control over parameter mapping, no transitive deps |
| **Resilience libraries** | `SqlRetryPolicy` + `InMemoryCircuitBreaker` | Tailored to our specific failure modes, no policy bloat |
| **Heavy ORM frameworks** | `Queries.cs` (raw SQL templates) | Precise control over locking hints, OUTPUT clauses, CTEs |
| **Mediator libraries** | `BusJobDispatcher` + Expression Trees | Compiled delegates instead of reflection-based dispatch |

::: warning 🎯 Design Decision
Even lightweight ORMs are dependencies. They bring `System.Data` extension methods that may conflict with other versions. ChokaQ's SqlMapper is 172 lines of code that does exactly what we need, nothing more.
:::

## What Makes It Enterprise-Grade?

### 1. Atomic State Transitions
Every move between pillars uses `INSERT...SELECT + DELETE` in a single SQL batch. No distributed transactions. No two-phase commit. If the server crashes mid-operation, the job stays in the source table.

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
```
No extra packages needed. Just listen to the `"ChokaQ"` meter.

## When to Choose ChokaQ

✅ **Ideal for:**
- SQL Server-centric environments
- Zero dependency conflict requirements
- Real-time dashboard with edit capabilities
- Database-level concurrency isolation (Bulkhead)
- Compliance environments requiring minimal attack surface (fewer deps = fewer CVEs)
- Teams wanting full control over every line of infrastructure code

❌ **Not designed for:**
- Cross-service pub/sub messaging patterns
- PostgreSQL or Redis storage (SQL Server only, by design)
- Recurring/scheduled jobs (not yet supported)
- Multi-region distributed coordination

<br>

> *Convinced? Jump to the [Getting Started](/getting-started) guide or dive into the [Three Pillars Architecture](/1-architecture/three-pillars).*
