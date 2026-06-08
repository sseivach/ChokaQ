# ChokaQ

## Documentation Site

**Read the full documentation:** [https://sseivach.github.io/ChokaQ/](https://sseivach.github.io/ChokaQ/)

![.NET 10](https://img.shields.io/badge/.NET-10.0%20-blue)
![License](https://img.shields.io/badge/License-Apache_2.0-green)
![Blazor](https://img.shields.io/badge/UI-Blazor%20Server-purple)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Public preview. The consumer install target is the top-level `ChokaQ` package; package validation uses `samples/ChokaQ.Sample.NuGetLab` while the API and The Deck continue to evolve.

**ChokaQ** is a .NET 10 background job processor for SQL Server-centric systems where reliability, observability, and a minimal dependency footprint matter. It bridges the gap between simple in-memory channels and heavy job frameworks by combining durable SQL storage, atomic state transitions, worker ownership, The Deck dashboard, and detailed architecture documentation.

The docs explain both setup and runtime behavior: backpressure, circuit breakers,
bulkheads, leases, idempotency, zombie recovery, and observability are documented
as practical parts of operating the system.

If you are evaluating ChokaQ operationally, start with the [SLOs And Alerts](https://sseivach.github.io/ChokaQ/5-operations/slo-alerts) and [Operations Runbooks](https://sseivach.github.io/ChokaQ/5-operations/runbooks). Those pages explain what queue lag, DLQ rate, worker health, throttling, timeouts, and state-transition conflicts mean in practice, plus the safe first actions when something goes wrong.

Before using ChokaQ for side-effecting work, read the [Delivery Guarantees](https://sseivach.github.io/ChokaQ/delivery-guarantees). The short version: ChokaQ provides at-least-once execution. It does not provide exactly-once external side effects, so handlers that send email, charge cards, call APIs, or update other systems must be idempotent.

![ChokaQ Dashboard](scr1.jpg)
![ChokaQ Dashboard - Ops Panel](scr2.jpg)

---

## Strategic Architecture

ChokaQ implements a **2x2 matrix architecture**, allowing developers to choose between volatile memory for speed and persistent SQL storage for reliability, combined with either typed Bus processing or raw Pipe processing.

### Processing Modes
1.  **Bus Mode:** Strongly typed jobs with dedicated handlers, Dependency Injection scopes, and profiles. Best for complex business logic.
2.  **Pipe Mode:** High-throughput processing of raw payloads via a single global handler. Best for telemetry, logs, and event streams.



### Storage Modes
1.  **In-Memory:** Zero-config RAM storage using System.Threading.Channels.
2.  **SQL Server:** Persistent storage using a custom lightweight ADO.NET wrapper (SqlMapper).

---

## Delivery Guarantees

ChokaQ is an **at-least-once** background job processor.

SQL Server mode is the durable production path: accepted jobs are stored in `JobsHot`, workers claim rows with ownership, and finalization moves jobs atomically to Archive, DLQ, or delayed retry. A stale worker that has lost ownership should not be able to finalize work owned by another worker.

At-least-once still means a handler may run more than once. A worker can crash or lose ownership after user code has already sent an email, charged a payment, or updated another system but before finalization is committed. ChokaQ cannot make those external side effects exactly-once. Use idempotency keys, unique constraints, outbox patterns, or provider-supported idempotency for side-effecting handlers.

`Fetched` jobs that never started user code can safely return to `Pending`. `Processing` jobs with expired heartbeats move to DLQ as `Zombie` jobs for operator review, because automatically retrying started work can duplicate side effects.

In-memory mode is process-local and non-durable. Its bounded channel is the volatile execution notification source, and Hot rows are control/audit rows used to reject stale channel items. Orphaned Hot rows can remain after a process crash and are not recovered across restart. Use it for demos, tests, local development, or volatile workloads where losing process-local work is acceptable. Use SQL Server mode for restart-safe production work.

See [Delivery Guarantees](https://sseivach.github.io/ChokaQ/delivery-guarantees) for the full contract, including timeout, cancellation, shutdown, type-key, and payload compatibility guidance.

For operational readiness, start with the [Operations Runbooks](https://sseivach.github.io/ChokaQ/5-operations/runbooks) and [SLOs And Alerts](https://sseivach.github.io/ChokaQ/5-operations/slo-alerts). They describe the signals and response paths that matter when ChokaQ is running real workloads.

---

## Data Architecture: The Three Pillars

To keep hot-path queries focused on active work, data is physically separated into three atomic tiers:

1.  **JobsHot (Active):**
    * Stores only Pending, Fetched, and Processing jobs.
    * Uses row-level locking (UPDLOCK, READPAST) for high-concurrency fetching without deadlocks.

2.  **JobsArchive (History):**
    * Stores successfully completed jobs.
    * Uses SQL Page Compression.
    * Optimized for analytical filtering (Date, Queue, Type).

3.  **JobsDLQ (Dead Letter Queue):**
    * Stores failed (MaxRetriesExceeded), cancelled, or zombie jobs.
    * Supports manual payload editing and resurrection.



---

## Technical Capabilities

### Core Engine
* **Minimal Dependency Footprint:** The Core library uses standard Microsoft.Extensions abstractions. SQL Server storage uses the official `Microsoft.Data.SqlClient` driver. ChokaQ intentionally avoids EF Core, Dapper, Polly, mediator libraries, and other third-party infrastructure dependencies.
* **Near-Native Execution:** Performance-optimized job dispatching using cached compiled Expression Trees, completely eliminating the overhead of standard reflection.
* **Atomic State Transitions:** All lifecycle events use SQL transactions with the `OUTPUT` clause to ensure data is never lost during movement between Hot, Archive, and DLQ tables.

### Concurrency
* **Dynamic Concurrency:** The number of active workers can be scaled up or down at runtime via the dashboard without restarting the application (implemented via `DynamicConcurrencyLimiter`).
* **Bulkhead Isolation:** Advanced queue management that prevents the "noisy neighbor" problem. By enforcing concurrency limits at the database level, the system ensures that resource-intensive tasks do not stall high-priority, lightweight operations.
* **Prefetching:** Workers use an internal bounded channel to buffer jobs from SQL, decoupling database latency from processing throughput.
* **Backpressure Policy:** SQL mode uses `JobsHot` as the durable backlog and keeps prefetch memory bounded; in-memory mode uses a bounded channel plus `ChokaQ:InMemory:MaxCapacity` to keep process-local pressure explicit.

### Resilience & Reliability
* **Smart Worker Logic:** Intelligent failure handling that distinguishes between transient and fatal errors. Non-transient exceptions bypass retry policies and are routed to the DLQ immediately to preserve system resources and provide instant visibility into code defects.
* **Circuit Breaker:** In-memory protection that tracks failure rates per Job Type. Automatically opens the circuit to block execution of failing job types, preventing cascading system failures.
* **Zombie Rescue:** Built-in recovery via a background service (`ZombieRescueService`) that monitors heartbeats. Abandoned `Fetched` jobs can return to `Pending`; expired `Processing` jobs move to DLQ as zombies so operators can inspect them before retrying side-effecting work.
* **Smart Retries:** Configurable exponential backoff strategies with jitter to prevent "thundering herd" effects on external services.
* **Runtime Configuration:** Execution timeouts, retry attempts, backoff, jitter, heartbeat windows, zombie recovery scans, SQL polling, cleanup batch size, transient SQL retry policy, health thresholds, and metric cardinality budgets can be bound from `appsettings.json` through `IConfiguration`.
* **Idempotency:** Built-in Hot-queue deduplication for active jobs via idempotency keys, plus optional completion-marker middleware for completed work. Application handlers remain responsible for idempotent external side effects.

### "The Deck" (Dashboard)
A reactive administrative dashboard built with **Blazor Server** and **SignalR**. It features real-time job monitoring, swappable visual themes, and specialized tools for operational management.

* **Visual Themes:** Includes two built-in themes: **Blueprint** (Engineering Blue) for clarity and **Carbon** (High-Contrast Dark) for low-light environments.
* **Queue Management:** Queues can be paused, resumed, or deactivated at runtime. Changes propagate immediately to all workers via the database.
* **Live Matrix:** Virtualized grid for active jobs.
* **Ops Panel:**
    * **Inspector:** View full job details and exception stack traces.
    * **Administrative Control (Hot-Edit & Resurrect):** Modify job payloads or tags directly within the Dead Letter Queue to fix data-driven failures and restart execution immediately, eliminating the need for manual database scripts or job re-enqueuing.
    * **History Filter:** Server-side filtering of the Archive table.
* **Bulk Actions:** Retry, Cancel, or Purge jobs in batches.
* **Circuits View:** Monitor the state (Closed/Open/HalfOpen) of all circuit breakers.
* **Console Stream:** System-wide events and logs are streamed directly to the browser console view.

---

## Observability (OpenTelemetry)

ChokaQ provides native metrics through standard `System.Diagnostics.Metrics`. This lets hosts monitor throughput, failures, and execution latency without ChokaQ owning your telemetry exporter choice.

**Available Metrics (Meter: `ChokaQ`):**

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `chokaq.jobs.enqueued` | Counter | none | `queue`, `type` |
| `chokaq.jobs.completed` | Counter | none | `queue`, `type` |
| `chokaq.jobs.failed` | Counter | none | `queue`, `type`, `error` |
| `chokaq.jobs.processing_duration` | Histogram | `ms` | `queue`, `type` |
| `chokaq.jobs.queue_lag` | Histogram | `ms` | `queue`, `type` |
| `chokaq.jobs.dlq` | Counter | none | `queue`, `type`, `reason` |
| `chokaq.jobs.retried` | Counter | none | `queue`, `type`, `attempt` |
| `chokaq.workers.active` | UpDownCounter | none | `queue` |
| `chokaq.jobs.heartbeat_failures` | Counter | none | `queue`, `type` |
| `chokaq.jobs.state_transition_conflicts` | Counter | none | `queue`, `type`, `transition` |
| `chokaq.idempotency.claims` | Counter | none | `outcome` |
| `chokaq.circuits.events` | Counter | none | `type`, `state`, `event` |

Metric tag cardinality is bounded by `ChokaQ:Metrics`. When a process sees more distinct queue, job type, error, or DLQ reason values than the configured budget, new values are emitted as `other` instead of creating unbounded monitoring time series.

### Health Checks
ChokaQ also contributes standard ASP.NET Core health checks so platform teams can publish the same `/health` endpoint they already use for probes and monitoring.

```csharp
builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

app.MapHealthChecks("/health");
```

The SQL integration registers three checks: `chokaq_sql` for storage reachability and schema readiness, `chokaq_worker` for hosted-worker liveness, and `chokaq_queue_saturation` for pending queue lag. The queue check maps lag into Healthy/Degraded/Unhealthy using `ChokaQ:Health` thresholds, so operators can tune readiness sensitivity per environment.

For alert design and incident response, see [SLOs And Alerts](https://sseivach.github.io/ChokaQ/5-operations/slo-alerts) and [Operations Runbooks](https://sseivach.github.io/ChokaQ/5-operations/runbooks).

### Structured Log Events
ChokaQ uses stable `EventId` values for lifecycle logs so operators can build SIEM queries, alerts, and runbooks without parsing free-form text.

| Range | Area |
|---|---|
| `1000-1099` | Worker service and loop lifecycle |
| `2000-2099` | Individual job execution lifecycle |
| `2100-2199` | State transitions and notifications |
| `3000-3099` | Enqueue producer boundary |
| `4000-4099` | Zombie rescue and recovery |
| `5000-5099` | SQL initialization and storage boundary |
| `6000-6099` | Operator/admin command boundary |

Important examples: `JobSucceededArchived` (`2003`), `JobRetriesExhaustedDlq` (`2023`), `ZombieJobsArchived` (`4002`), and `SqlInitializationFailed` (`5002`).

### Exporting Metrics to Prometheus / Grafana
Since ChokaQ uses standard BCL components, simply configure OpenTelemetry in your host `Program.cs` and listen to the **"ChokaQ"** meter:

```csharp
// Requires package: OpenTelemetry.Exporter.Prometheus.AspNetCore
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("ChokaQ"); // Listen to ChokaQ events
        metrics.AddPrometheusExporter();
    });

var app = builder.Build();

// Expose the /metrics endpoint
app.UseOpenTelemetryPrometheusScrapingEndpoint();
```

---

## Middleware Pipeline

ChokaQ supports an ASP.NET Core style Middleware Pipeline. You can intercept the execution of jobs to inject cross-cutting concerns like custom logging, validation, or correlation IDs without bloating your business logic.



### 1. Create a Middleware
Implement the `IChokaQMiddleware` interface. The `job` parameter will be your typed DTO in Bus Mode, or the raw JSON string in Pipe Mode.

```csharp
using ChokaQ.Abstractions.Contexts;
using ChokaQ.Abstractions.Middleware;

public class LoggingMiddleware : IChokaQMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;
    public LoggingMiddleware(ILogger<LoggingMiddleware> logger) => _logger = logger;

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        _logger.LogInformation("Job {JobId} starting...", context.JobId);
        
        // Pass control to the next middleware (or the final handler)
        await next();
        
        _logger.LogInformation("Job {JobId} finished successfully.", context.JobId);
    }
}
```

### 2. Register it
Middlewares are executed in the exact order they are registered.

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    
    // Will be executed first (outermost layer)
    options.AddMiddleware<LoggingMiddleware>(); 
});
```

---

## Security & Authorization

ChokaQ follows a **"Secure by Design / Inversion of Control"** philosophy. Instead of implementing its own authentication system or enforcing specific providers (like Identity or Entra ID), it leverages the standard **ASP.NET Core Authorization Policy** infrastructure.

You define *who* is an Admin in your host application, and ChokaQ respects it.

### Prerequisites
ChokaQ does **not** modify your middleware pipeline. You must ensure the standard ASP.NET Core middleware is registered in your `Program.cs` **before** calling `MapChokaQTheDeck()`:

```csharp
app.UseAuthentication(); // Required: populates HttpContext.User
app.UseAuthorization();  // Required: enforces [Authorize] policies

app.MapChokaQTheDeck();
```

### Enabling Security
The Deck is secure by default. If no named policy is configured, the Dashboard and SignalR Hub use the host application's default authorization policy. For production, provide explicit read/connect and optional destructive-command policies.

```csharp
// 1. Define policies in your host app (Program.cs)
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ChokaQAdmin", policy => 
        policy.RequireClaim("Role", "SystemAdministrator"));
});

// 2. Bind ChokaQ to these policies
builder.Services.AddChokaQTheDeck(options =>
{
    options.RoutePrefix = "/chokaq";
    
    // Read/connect access for the dashboard and SignalR Hub.
    options.AuthorizationPolicy = "ChokaQAdmin";

    // Optional stronger policy for cancel, purge, edit, pause, retry, and resurrect commands.
    options.DestructiveAuthorizationPolicy = "ChokaQAdmin";
});
```

Public access requires an explicit development/demo opt-in:

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.AllowAnonymousDeck = true;
});
```

---

## Quick Start

For package consumers, install the top-level `ChokaQ` package. It pulls the
runtime subpackages (`ChokaQ.Core`, `ChokaQ.Storage.SqlServer`,
`ChokaQ.TheDeck`, and `ChokaQ.Abstractions`) transitively.

### Docker Compose Sample

The repository includes a SQL Server + Bus sample compose file:

```powershell
docker compose up --build
```

Open `http://localhost:5299` for the launcher, `http://localhost:5299/chokaq`
for The Deck, and `http://localhost:5299/health` for health checks. See
[Docker Compose Sample](https://sseivach.github.io/ChokaQ/samples/docker-compose)
for details and reset commands.

### Local NuGet Lab

`samples/ChokaQ.Sample.NuGetLab` is the package-consumer smoke app. It restores
the top-level `ChokaQ` package from `artifacts/packages` through NuGet, not
through source project references, and exercises SQL Server, The Deck, health
checks, idempotency, retry/failure paths, delayed jobs, queue controls, and
runtime worker scaling.

See [Local NuGet Lab](https://sseivach.github.io/ChokaQ/samples/nuget-lab) or
`samples/ChokaQ.Sample.NuGetLab/README.md` for the local pack and run commands.
Use the lab as the final package-consumer smoke before publishing or promoting a
preview package.

### 1. Package Reference

Add the top-level package to your ASP.NET Core `.csproj` file:

```xml
<ItemGroup>
    <PackageReference Include="ChokaQ" Version="0.1.0-preview.1" />
</ItemGroup>
```

For source development inside this repository, the Bus and Pipe samples still
use project references. For package validation before promotion, use
`samples/ChokaQ.Sample.NuGetLab`, which restores `ChokaQ` from
`artifacts/packages` or another configured NuGet feed.

### 2. Configuration

**Program.cs**
```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Add ChokaQ Core & Profiles.
// Runtime policy is bound from appsettings; code owns compile-time registrations.
builder.Services.AddChokaQ(
    builder.Configuration.GetSection("ChokaQ"),
    options =>
    {
        // Register Job Profiles (Bus Mode)
        options.AddProfile<MailingProfile>();
    });

// 2. Add SQL Storage with Auto-Provisioning
// This will automatically create the 'chokaq' schema and tables on startup.
builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"));

// 3. Add Dashboard and health checks
builder.Services.AddChokaQTheDeck();
builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

var app = builder.Build();

// 4. Map Dashboard and health routes
app.MapChokaQTheDeck(); // Default route: /chokaq
app.MapHealthChecks("/health");

app.Run();
```

**appsettings.json**
```json
{
  "ChokaQ": {
    "Execution": {
      "DefaultTimeout": "00:15:00",
      "HeartbeatFailureThreshold": 10,
      "CancelOnHeartbeatFailure": false,
      "PendingCancellationRetention": "00:00:30"
    },
    "Retry": {
      "MaxAttempts": 3,
      "BaseDelay": "00:00:03",
      "MaxDelay": "01:00:00",
      "BackoffMultiplier": 2.0,
      "JitterMaxDelay": "00:00:01",
      "CircuitBreakerDelay": "00:00:05",
      "MaxJobAge": "1.00:00:00"
    },
    "Recovery": {
      "FetchedJobTimeout": "00:10:00",
      "ProcessingZombieTimeout": "00:10:00",
      "ScanInterval": "00:01:00"
    },
    "Worker": {
      "ShutdownGracePeriod": "00:00:30"
    },
    "Health": {
      "WorkerHeartbeatTimeout": "00:00:30",
      "QueueLagDegradedThreshold": "00:00:05",
      "QueueLagUnhealthyThreshold": "00:00:10"
    },
    "InMemory": {
      "MaxCapacity": 100000
    },
    "Metrics": {
      "MaxQueueTagValues": 100,
      "MaxJobTypeTagValues": 500,
      "MaxErrorTagValues": 100,
      "MaxFailureReasonTagValues": 50,
      "MaxTagValueLength": 128,
      "UnknownTagValue": "unknown",
      "OverflowTagValue": "other"
    },
    "Serialization": {
      "MaxPayloadBytes": 1000000
    },
    "Idempotency": {
      "InProgressTtl": "00:30:00",
      "DefaultResultTtl": null,
      "MinResultTtl": null,
      "MaxResultTtl": null
    },
    "TypeResolution": {
      "RequireRegisteredJobTypes": true
    },
    "Queues": {
      "reports": {
        "ExecutionTimeout": "01:00:00"
      }
    },
    "SqlServer": {
      "ConnectionString": "Server=localhost;Database=ChokaQ;Trusted_Connection=True;TrustServerCertificate=True;",
      "SchemaName": "chokaq",
      "AutoCreateSqlTable": true,
      "PollingInterval": "00:00:01",
      "NoQueuesSleepInterval": "00:00:05",
      "CommandTimeoutSeconds": 30,
      "CleanupBatchSize": 1000,
      "WorkerShutdownGracePeriod": "00:00:30",
      "PrefetchedJobReleaseTimeout": "00:00:05"
    }
  }
}
```

**Configuration quick reference**

| Parameter | Description |
|---|---|
| `Execution.DefaultTimeout` | Maximum wall-clock time a job handler may run before ChokaQ cancels it. |
| `Execution.HeartbeatFailureThreshold` | Consecutive heartbeat write failures before execution is marked heartbeat-degraded. |
| `Execution.CancelOnHeartbeatFailure` | If `true`, repeated heartbeat write failures cancel the running job. Default behavior is degraded telemetry plus zombie recovery. |
| `Execution.PendingCancellationRetention` | How long a cancellation request can wait for a matching execution token to appear. |
| `Retry.MaxAttempts` | Maximum total execution attempts, including the first run. |
| `Retry.BaseDelay` | Initial retry delay before exponential backoff is applied. |
| `Retry.MaxDelay` | Hard cap for calculated retry delays and throttled retry delays. |
| `Retry.BackoffMultiplier` | Exponential multiplier applied after each failed attempt. |
| `Retry.JitterMaxDelay` | Maximum random delay added to retries to avoid synchronized retry storms. |
| `Retry.CircuitBreakerDelay` | Delay used when the circuit breaker blocks execution. |
| `Retry.MaxJobAge` | Maximum wall-clock retry lifetime before the job moves to DLQ as `RetryLifetimeExpired`. |
| `Recovery.FetchedJobTimeout` | Time before a fetched-but-unstarted job is returned to `Pending`. |
| `Recovery.ProcessingZombieTimeout` | Time before a processing job with expired heartbeat moves to DLQ as `Zombie`. |
| `Recovery.ScanInterval` | How often recovery scans for abandoned fetched jobs and processing zombies. |
| `Worker.ShutdownGracePeriod` | Maximum in-memory worker loop shutdown wait. |
| `Health.WorkerHeartbeatTimeout` | Maximum age of the worker process heartbeat before health degrades. |
| `Health.QueueLagDegradedThreshold` | Queue lag threshold for degraded health. |
| `Health.QueueLagUnhealthyThreshold` | Queue lag threshold for unhealthy health. |
| `InMemory.MaxCapacity` | Maximum retained in-process rows before old Archive/DLQ history is evicted. |
| `Metrics.MaxQueueTagValues` | Maximum distinct queue tag values emitted by one process. |
| `Metrics.MaxJobTypeTagValues` | Maximum distinct job type tag values emitted by one process. |
| `Metrics.MaxErrorTagValues` | Maximum distinct error tag values emitted by one process. |
| `Metrics.MaxFailureReasonTagValues` | Maximum distinct failure reason tag values emitted by one process. |
| `Metrics.MaxTagValueLength` | Maximum metric tag value length before trimming. |
| `Metrics.UnknownTagValue` | Replacement value for blank metric tags. |
| `Metrics.OverflowTagValue` | Replacement value after a tag-cardinality budget is exhausted. |
| `Serialization.MaxPayloadBytes` | Maximum serialized job payload size accepted before storage mutation. |
| `Idempotency.InProgressTtl` | Lease duration for an active idempotency claim. |
| `Idempotency.DefaultResultTtl` | Default completion marker TTL when a job does not provide one. |
| `Idempotency.MinResultTtl` | Minimum allowed completion marker TTL. |
| `Idempotency.MaxResultTtl` | Maximum allowed completion marker TTL. |
| `TypeResolution.RequireRegisteredJobTypes` | Requires Bus jobs to use registered stable type keys instead of compatibility fallback identity. |
| `Queues:{name}:ExecutionTimeout` | Per-queue handler execution timeout override. |
| `SqlServer.ConnectionString` | SQL Server connection string for durable storage. |
| `SqlServer.SchemaName` | Database schema used for ChokaQ tables and procedures. |
| `SqlServer.AutoCreateSqlTable` | If `true`, ChokaQ attempts to create/update its SQL schema at startup. |
| `SqlServer.PollingInterval` | SQL worker polling interval when active queues exist but no jobs are due. |
| `SqlServer.NoQueuesSleepInterval` | SQL worker sleep duration when all queues are paused or inactive. |
| `SqlServer.CommandTimeoutSeconds` | Timeout for ChokaQ SQL commands, separate from handler execution timeout. |
| `SqlServer.CleanupBatchSize` | Maximum Archive/DLQ rows deleted by one cleanup transaction. |
| `SqlServer.WorkerShutdownGracePeriod` | Maximum SQL worker wait for active processing tasks during shutdown. |
| `SqlServer.PrefetchedJobReleaseTimeout` | Maximum time spent releasing one prefetched unstarted job during pause or shutdown. |

See the [Runtime Configuration](https://sseivach.github.io/ChokaQ/configuration)
reference for the full runtime configuration and operational guidance.

See [Backpressure Policy](https://sseivach.github.io/ChokaQ/3-deep-dives/backpressure-policy)
for the SQL and in-memory backpressure model.

### 3. Define a Job & Handler

Every typed ChokaQ job is a DTO that implements `IChokaQJob`.

The shortest path is to inherit from `ChokaQBaseJob`. `ChokaQBaseJob` is a C#
`record`, so the derived DTO must also be a `record`; a `class` cannot inherit
from it. This base type gives each new job a generated `Id`.

```csharp
// Job Contract (DTO). Because ChokaQBaseJob is a record, this is a record too.
public record SendEmailJob(string To) : ChokaQBaseJob;
```

If you prefer a regular mutable class, implement `IChokaQJob` directly and
provide the job `Id` yourself:

```csharp
public sealed class SendEmailJob : IChokaQJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string To { get; set; } = string.Empty;
}
```

Both shapes work with the same handler and profile registration. Use
`ChokaQBaseJob` when you want concise immutable DTOs and automatic ID creation;
use `IChokaQJob` directly when your message contract needs ordinary class
semantics, mutable setters, or custom ID generation.

See [Job Contracts](https://sseivach.github.io/ChokaQ/job-contracts) for DTO
shape, serialization, type-key, and idempotency guidance.

```csharp
// Job Handler
public class EmailHandler : IChokaQJobHandler<SendEmailJob>
{
    public async Task HandleAsync(SendEmailJob job, CancellationToken ct)
    {
        // Business logic here
        await Task.Delay(100);
    }
}

// Profile Registration
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
    }
}
```

### 4. Enqueue Job

```csharp
// Inject IChokaQQueue into your controller or service
await queue.EnqueueAsync(new SendEmailJob("user@example.com"), priority: 10);
```

For the class-based DTO shape, enqueue with an object initializer instead:

```csharp
await queue.EnqueueAsync(
    new SendEmailJob { To = "user@example.com" },
    priority: 10);
```

---

## License

Copyright © 2026 Sergei Seivach

This project is licensed under the [Apache License 2.0](LICENSE).
