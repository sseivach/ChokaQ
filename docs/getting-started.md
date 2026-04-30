# Getting Started

## Prerequisites

- **.NET 10 SDK**
- **SQL Server** (for persistent mode) — or use In-Memory for development

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

// 1. Add ChokaQ Core with Profiles
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    options.MaxRetries = 3;
    options.RetryDelaySeconds = 3;
    options.ZombieTimeoutSeconds = 600;
});

// 2. Add SQL Server Storage (Three Pillars)
builder.Services.UseSqlServer(options =>
{
    options.ConnectionString = builder.Configuration
        .GetConnectionString("DefaultConnection");
    options.SchemaName = "chokaq";
    options.AutoCreateSqlTable = true; // Auto-provisions on first start
});

// 3. Add Dashboard
builder.Services.AddChokaQTheDeck();

var app = builder.Build();

// 4. Map Dashboard Endpoint
app.MapChokaQTheDeck(); // Available at /chokaq

app.Run();
```

::: tip 💡 Auto-Provisioning
With `AutoCreateSqlTable = true`, ChokaQ will automatically create the `chokaq` schema and all five tables (JobsHot, JobsArchive, JobsDLQ, StatsSummary, Queues) with optimized indexes on first startup. Completely idempotent — safe to run on every restart.
:::

### 2. Define a Job & Handler

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

### 3. Register in a Profile

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

### 4. Enqueue a Job

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
| [Three Pillars](/1-architecture/three-pillars) | Why we physically separate Hot, Archive, and DLQ |
| [State Machine](/2-lifecycle/state-machine) | The full lifecycle of a job from Pending to Archive |
| [The Deck](/4-the-deck/realtime-signalr) | Real-time admin dashboard with SignalR |
