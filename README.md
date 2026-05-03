# ChokaQ

![.NET 10](https://img.shields.io/badge/.NET-10.0%20-blue)
![License](https://img.shields.io/badge/License-Apache_2.0-green)
![Blazor](https://img.shields.io/badge/UI-Blazor%20Server-purple)
![Status](https://img.shields.io/badge/status-Active%20Development-orange)

**Current Status:** Active development / production-preview hardening. ChokaQ is not published as a NuGet package yet; use source references or the Docker Compose sample while the API and The Deck continue to evolve.

**ChokaQ** is a .NET 10 background job processor for SQL Server-centric systems where reliability, observability, and a minimal dependency footprint matter. It bridges the gap between simple in-memory channels and heavy job frameworks by combining durable SQL storage, atomic state transitions, worker ownership, The Deck dashboard, and detailed architecture documentation.

ChokaQ is also becoming a learning project: the codebase and docs are intended to explain production patterns such as backpressure, circuit breakers, bulkheads, leases, idempotency, zombie recovery, and observability in the context of one working system.

Use the docs site as both product documentation and a study map. The [Architecture Learning Track](docs/learning-track.md) explains how each production pattern should be read, tested, operated, and discussed in senior/staff architecture interviews.

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
* **Zombie Rescue:** Built-in self-healing mechanisms via a background service (`ZombieRescueService`) that monitors heartbeats. Automatically recovers abandoned or "zombie" jobs, ensuring no task is lost due to process crashes or network instability.
* **Smart Retries:** Configurable exponential backoff strategies with jitter to prevent "thundering herd" effects on external services.
* **Runtime Configuration:** Execution timeouts, retry attempts, backoff, jitter, heartbeat windows, zombie recovery scans, SQL polling, cleanup batch size, transient SQL retry policy, health thresholds, and metric cardinality budgets can be bound from `appsettings.json` through `IConfiguration`.
* **Idempotency:** Built-in Hot-queue deduplication for active jobs via idempotency keys, plus optional result-idempotency middleware for completed work.

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

Metric tag cardinality is bounded by `ChokaQ:Metrics`. When a process sees more distinct queue, job type, error, or DLQ reason values than the configured budget, new values are emitted as `other` instead of creating unbounded monitoring time series.

### Health Checks
ChokaQ also contributes standard ASP.NET Core health checks so platform teams can publish the same `/health` endpoint they already use for probes and monitoring.

```csharp
builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

app.MapHealthChecks("/health");
```

The SQL integration registers three checks: `chokaq_sql` for storage reachability and schema readiness, `chokaq_worker` for hosted-worker liveness, and `chokaq_queue_saturation` for pending queue lag. The queue check maps lag into Healthy/Degraded/Unhealthy using `ChokaQ:Health` thresholds, so operators can tune readiness sensitivity per environment.

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

## Quick Start (Source Integration)

Since ChokaQ is currently in development, integrate it by referencing the source projects directly.

### Docker Compose Sample

The repository includes a SQL Server + Bus sample compose file:

```powershell
docker compose up --build
```

Open `http://localhost:5299` for the launcher, `http://localhost:5299/chokaq`
for The Deck, and `http://localhost:5299/health` for health checks. See
`docs/samples/docker-compose.md` for details and reset commands.

### 1. Project References

Add references to the core libraries in your ASP.NET Core `.csproj` file:

```xml
<ItemGroup>
    <ProjectReference Include="..\src\ChokaQ.Core\ChokaQ.Core.csproj" />
    <ProjectReference Include="..\src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj" />
    <ProjectReference Include="..\src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj" />
</ItemGroup>
```

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
      "DefaultTimeout": "00:15:00"
    },
    "Retry": {
      "MaxAttempts": 3,
      "BaseDelay": "00:00:03",
      "MaxDelay": "01:00:00",
      "BackoffMultiplier": 2.0,
      "JitterMaxDelay": "00:00:01"
    },
    "Recovery": {
      "FetchedJobTimeout": "00:10:00",
      "ProcessingZombieTimeout": "00:10:00",
      "ScanInterval": "00:01:00"
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
      "CleanupBatchSize": 1000
    }
  }
}
```

See `docs/configuration.md` for the full runtime configuration reference.

See `docs/3-deep-dives/backpressure-policy.md` for the SQL and in-memory backpressure model.

### 3. Define a Job & Handler

```csharp
// Job Contract (DTO)
public record SendEmailJob(string To) : ChokaQBaseJob;

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
        CreateJob<SendEmailJob, EmailHandler>("email_v1");
    }
}
```

### 4. Enqueue Job

```csharp
// Inject IChokaQQueue into your controller or service
await queue.EnqueueAsync(new SendEmailJob("user@example.com"), priority: 10);
```

---

## License

Copyright © 2026 Sergei Seivach

This project is licensed under the [Apache License 2.0](LICENSE).
