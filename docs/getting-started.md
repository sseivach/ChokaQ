# Getting Started

## Prerequisites

- **.NET 10 SDK**
- **SQL Server** (for persistent mode) — or use In-Memory for development

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

The full configuration reference is in [Runtime Configuration](/configuration). ChokaQ validates unsafe values at startup, so bad timeouts, empty SQL connection strings, or non-positive cleanup batch sizes fail before workers can claim production jobs.

`/health` is a normal ASP.NET Core health-check endpoint. In SQL Server mode it reports SQL schema reachability, worker liveness, and queue saturation. Keep the endpoint protected or network-scoped in production if your health payloads are visible outside trusted infrastructure.

OpenTelemetry metrics use the `ChokaQ` meter. `ChokaQ:Metrics` caps tag cardinality so accidental dynamic queue names, job types, errors, or DLQ reasons collapse into `other` instead of creating unbounded monitoring time series.

The Deck is secure by default. `AddChokaQTheDeck()` maps the UI and SignalR Hub
with authorization required unless you explicitly set `AllowAnonymousDeck = true`
for a local demo or public sandbox.

::: tip 💡 Auto-Provisioning
With `AutoCreateSqlTable = true`, ChokaQ will automatically create the `chokaq` schema, all runtime tables (JobsHot, JobsArchive, JobsDLQ, StatsSummary, Queues), the SchemaMigrations ledger, and optimized indexes on first startup. Completely idempotent — safe to run on every restart.
:::

### 3. Define a Job & Handler

```csharp
// Job DTO (the contract)
public record SendEmailJob(string To, string Subject) : ChokaQBaseJob;

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
        CreateJob<SendEmailJob, EmailHandler>("email_v1");
    }
}
```

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

Jobs are stored in `ConcurrentDictionary` and processed via `System.Threading.Channels`. No database needed.

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

## What's Next?

| Topic | What You'll Learn |
|-------|-------------------|
| [Why ChokaQ?](/why-chokaq) | The core philosophy and architectural benefits |
| [Architecture Learning Track](/learning-track) | The study map for production patterns and interview framing |
| [Three Pillars](/1-architecture/three-pillars) | Why we physically separate Hot, Archive, and DLQ |
| [State Machine](/2-lifecycle/state-machine) | The full lifecycle of a job from Pending to Archive |
| [The Deck](/4-the-deck/realtime-signalr) | Real-time admin dashboard with SignalR |
