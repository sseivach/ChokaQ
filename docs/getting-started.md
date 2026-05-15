# Getting Started

This page gets a ChokaQ host running and explains what each piece is doing.

If you are new to ChokaQ, read [Overview](/overview) first.
If you want to run something immediately, use the Docker sample below.

## Prerequisites

- **.NET 10 SDK**
- **SQL Server** (for persistent mode) — or use In-Memory for development

## The Smallest Useful Mental Model

Before the code, here is the shape of the system:

| Piece | What you add | What it does |
|---|---|---|
| Job DTO | A class or record that implements `IChokaQJob`. | Describes the work to do. |
| Handler | `IChokaQJobHandler<TJob>`. | Runs your business logic. |
| Profile | `ChokaQJobProfile`. | Connects job type keys to handlers. |
| Storage | SQL Server or in-memory. | Stores active work and final states. |
| Worker | Registered by ChokaQ. | Claims jobs and runs handlers. |
| The Deck | `/chokaq`. | Shows jobs, queues, health, failures, and controls. |

The normal path is:

```text
enqueue -> JobsHot -> worker claim -> handler -> Archive or DLQ
```

For delayed jobs and retries, the row stays in `JobsHot` with a future
`ScheduledAtUtc`. Workers ignore it until it becomes due.

## Try The SQL Sample With Docker

The repository includes a Docker Compose sample that starts SQL Server and the
Bus sample app together:

```powershell
docker compose up --build
```

Then open:

```text
http://localhost:5299
http://localhost:5299/chokaq
http://localhost:5299/health
```

The full walkthrough is in [Docker Compose Sample](/samples/docker-compose).

## Project References

Since ChokaQ is currently in active development, integrate it by referencing the source projects:

```xml
<ItemGroup>
    <ProjectReference Include="..\src\ChokaQ.Core\ChokaQ.Core.csproj" />
    <ProjectReference Include="..\src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj" />
    <ProjectReference Include="..\src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj" />
</ItemGroup>
```

## Minimal Setup (Bus Mode + SQL Server)

Bus mode means each job has a typed DTO and a typed handler. This is the best
starting point for normal application work.

### 1. Configure `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Add ChokaQ Core with Profiles.
// Runtime policy is bound from appsettings; code owns compile-time registrations.
builder.Services.AddChokaQ(
    builder.Configuration.GetSection("ChokaQ"),
    options =>
    {
        options.AddProfile<MailingProfile>();
    });

// 2. Add SQL Server Storage (Three Pillars)
builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"));

// 3. Add Dashboard
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddChokaQTheDeck();

// 4. Add standard ASP.NET Core health checks.
builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

var app = builder.Build();

// 5. Map Dashboard and health endpoints.
app.MapChokaQTheDeck(); // Available at /chokaq
app.MapHealthChecks("/health");

app.Run();
```

### 2. Configure `appsettings.json`

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

The full configuration reference is in [Runtime Configuration](/configuration). ChokaQ validates unsafe values at startup, so bad timeouts, empty SQL connection strings, or non-positive cleanup batch sizes fail before workers can claim production jobs.

Read [Delivery Guarantees](/delivery-guarantees) before running side-effecting
work. ChokaQ provides at-least-once execution, not exactly-once external side
effects. Handlers that send email, charge cards, call APIs, publish messages, or
update other systems should use stable idempotency keys, unique constraints,
outbox patterns, or provider-supported idempotency.

`/health` is a normal ASP.NET Core health-check endpoint. In SQL Server mode it reports SQL schema reachability, worker liveness, and queue saturation. Keep the endpoint protected or network-scoped in production if your health payloads are visible outside trusted infrastructure.

OpenTelemetry metrics use the `ChokaQ` meter. `ChokaQ:Metrics` caps tag cardinality so accidental dynamic queue names, job types, errors, or DLQ reasons collapse into `other` instead of creating unbounded monitoring time series.

The Deck is secure by default. `AddChokaQTheDeck()` maps the UI and SignalR Hub
with authorization required unless you explicitly set `AllowAnonymousDeck = true`
for a local demo or public sandbox.

::: tip 💡 Auto-Provisioning
With `AutoCreateSqlTable = true`, ChokaQ will automatically create the `chokaq` schema, all runtime tables (JobsHot, JobsArchive, JobsDLQ, StatsSummary, MetricBuckets, Queues), the SchemaMigrations ledger, and optimized indexes on first startup. Completely idempotent — safe to run on every restart.
:::

### 3. Define a Job & Handler

Every typed ChokaQ job is a DTO that implements `IChokaQJob`. You have two
supported shapes:

- Inherit from `ChokaQBaseJob` for a compact immutable contract with an
  automatically generated `Id`.
- Implement `IChokaQJob` directly when you want a normal class, mutable
  properties, or custom ID generation.

`ChokaQBaseJob` is itself a C# `record`, so any DTO that inherits from it must
also be a `record`. This is a C# language rule, not a ChokaQ runtime
restriction. Do not write `class MyJob : ChokaQBaseJob`; the compiler will
reject it. If you want a class, implement `IChokaQJob` directly.

```csharp
// Job DTO (the contract). ChokaQBaseJob is a record, so this DTO is a record.
public record SendEmailJob(string To, string Subject) : ChokaQBaseJob;
```

The inherited `Id` is generated when the job object is created. Put only your
business fields in the positional record constructor.

Equivalent class-based contract:

```csharp
public sealed class SendEmailJob : IChokaQJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}
```

Use the class form if your existing application already uses mutable DTOs, if
your serializer or mapper expects a parameterless constructor, or if the job ID
must come from your own correlation/idempotency scheme. Handlers, queueing,
storage, retries, and The Deck work the same way for both forms.

For deeper guidance on serialization, type-key versioning, and idempotency
keys, see [Job Contracts](/job-contracts).

```csharp
// Handler (the business logic)
public class EmailHandler : IChokaQJobHandler<SendEmailJob>
{
    private readonly IEmailService _emailService;

    public EmailHandler(IEmailService emailService)
        => _emailService = emailService;

    public async Task HandleAsync(SendEmailJob job, CancellationToken ct)
    {
        await _emailService.SendAsync(job.To, job.Subject, ct);
    }
}
```

### 4. Register in a Profile

Profiles group related jobs together. The `typeKey` is the string identifier stored in the database — it decouples your CLR type name from persistence.

```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
    }
}
```

The `typeKey` is persisted with the job. Prefer stable semantic keys such as
`email.send.v1` over CLR class names so refactors do not change the stored
message contract.

### 5. Enqueue a Job

```csharp
// Inject IChokaQQueue into your controller or service
public class OrderController : ControllerBase
{
    private readonly IChokaQQueue _queue;

    public OrderController(IChokaQQueue queue) => _queue = queue;

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(OrderDto order)
    {
        // Process order...

        // Fire-and-forget: send confirmation email
        await _queue.EnqueueAsync(
            new SendEmailJob(order.Email, "Order Confirmed"),
            priority: 10
        );

        // If SendEmailJob is the class-based DTO shape instead:
        // await _queue.EnqueueAsync(
        //     new SendEmailJob
        //     {
        //         To = order.Email,
        //         Subject = "Order Confirmed"
        //     },
        //     priority: 10
        // );

        return Ok();
    }
}
```

## In-Memory Mode (No SQL Server Required)

For development and testing, skip `UseSqlServer()` entirely:

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    options.ConfigureInMemory(o => o.MaxCapacity = 10_000);
});
```

Jobs are stored in process memory and processed via `System.Threading.Channels`.
No database is needed, but this mode is not durable. If the process exits,
in-memory jobs and history are lost. Use SQL Server mode for restart-safe
production work.

## Pipe Mode (High-Throughput)

For raw event streaming without typed handlers:

```csharp
builder.Services.AddChokaQ(options =>
{
    options.UsePipe<GlobalEventHandler>();
});

// Handler receives raw JSON strings
public class GlobalEventHandler : IChokaQPipeHandler
{
    public async Task HandleAsync(string jobType, string payload, CancellationToken ct)
    {
        // Process raw payload
        Console.WriteLine($"[{jobType}]: {payload}");
    }
}
```

## Adding Middleware

Intercept every job execution with ASP.NET Core-style middleware:

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    options.AddMiddleware<LoggingMiddleware>();
    options.AddMiddleware<CorrelationIdMiddleware>();
});

public class LoggingMiddleware : IChokaQMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
        => _logger = logger;

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        _logger.LogInformation("Job {JobId} starting...", context.JobId);
        await next(); // Call next middleware (or the handler)
        _logger.LogInformation("Job {JobId} finished.", context.JobId);
    }
}
```

Middleware runs around handler dispatch only. It is the right place for logging,
correlation, audit tags, validation, timing, and idempotency guards. It does not
own fetch, worker lease acquisition, circuit breaker decisions, retry
scheduling, Archive, or DLQ transitions; those remain processor/storage
lifecycle policies.

Middleware executes in registration order around the handler. In Bus mode the
`job` argument is the deserialized DTO. In Pipe mode it is the raw payload
string.

## What To Check After It Runs

Open `/chokaq` and look for these signals:

| Signal | Meaning |
|---|---|
| Pending | Jobs waiting for a worker. |
| Buffered or Fetched | Jobs claimed by workers but not yet running user code. |
| Processing | Handlers currently executing. |
| Succeeded | Jobs moved to Archive. |
| Failed or DLQ | Jobs that need inspection or repair. |
| Queue lag | How long eligible work has waited. |

Then open `/health`. In SQL Server mode, health checks verify SQL schema access,
worker liveness, and queue saturation.

## What's Next?

| Topic | What You'll Learn |
|-------|-------------------|
| [Overview](/overview) | The short model of the product and architecture |
| [Why ChokaQ?](/why-chokaq) | The core philosophy and architectural benefits |
| [Delivery Guarantees](/delivery-guarantees) | What ChokaQ promises and what your handlers must handle |
| [Three Pillars](/1-architecture/three-pillars) | Why we physically separate Hot, Archive, and DLQ |
| [State Machine](/2-lifecycle/state-machine) | The full lifecycle of a job from Pending to Archive |
| [The Deck](/4-the-deck/realtime-signalr) | Real-time admin dashboard with SignalR |
