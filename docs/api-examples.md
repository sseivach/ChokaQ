# API Examples

This page collects small, copyable examples for the public ChokaQ APIs. Start
with [Getting Started](/getting-started) for the full walkthrough; use this page
when you already know the concept and need the shape of the code.

## Minimal SQL Server Host

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChokaQ(
    builder.Configuration.GetSection("ChokaQ"),
    options =>
    {
        options.AddProfile<BillingProfile>();
    });

builder.Services.UseSqlServer(
    builder.Configuration.GetSection("ChokaQ:SqlServer"));

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddChokaQTheDeck();

builder.Services.AddHealthChecks()
    .AddChokaQSqlServerHealthChecks(builder.Configuration.GetSection("ChokaQ:Health"));

var app = builder.Build();

app.MapChokaQTheDeck();
app.MapHealthChecks("/health");

app.Run();
```

## Typed Job Contract

```csharp
public record CapturePaymentJob(
    string PaymentId,
    decimal Amount,
    string Currency) : ChokaQBaseJob;
```

Use a stable business identifier in the payload. The ChokaQ job ID identifies
the queued work item; your business ID identifies the external operation.

## Typed Handler

```csharp
public sealed class CapturePaymentHandler
    : IChokaQJobHandler<CapturePaymentJob>
{
    private readonly IPaymentGateway _gateway;

    public CapturePaymentHandler(IPaymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task HandleAsync(CapturePaymentJob job, CancellationToken ct)
    {
        await _gateway.CaptureAsync(
            paymentId: job.PaymentId,
            amount: job.Amount,
            currency: job.Currency,
            idempotencyKey: job.PaymentId,
            ct);
    }
}
```

Handlers must be safe for at-least-once execution. ChokaQ can preserve and retry
work, but external side effects still need business idempotency.

## Job Profile

```csharp
public sealed class BillingProfile : ChokaQJobProfile
{
    public BillingProfile()
    {
        CreateJob<CapturePaymentJob, CapturePaymentHandler>("billing.capture-payment.v1");
    }
}
```

The type key is persisted in SQL. Prefer semantic versioned keys over CLR type
names so refactors do not break old rows.

## Enqueue Now

```csharp
public sealed class BillingService
{
    private readonly IChokaQQueue _queue;

    public BillingService(IChokaQQueue queue)
    {
        _queue = queue;
    }

    public Task CaptureAsync(string paymentId, decimal amount, CancellationToken ct)
    {
        return _queue.EnqueueAsync(
            new CapturePaymentJob(paymentId, amount, "USD"),
            queue: "billing",
            priority: 20,
            idempotencyKey: $"capture:{paymentId}",
            ct: ct);
    }
}
```

The enqueue idempotency key prevents duplicate active jobs with the same
business key. It does not replace idempotency inside the handler.

## Enqueue With Delay

```csharp
await queue.EnqueueAsync(
    new CapturePaymentJob(paymentId, amount, "USD"),
    queue: "billing",
    delay: TimeSpan.FromMinutes(10),
    ct: ct);
```

Delayed execution is stored as `ScheduledAtUtc`. Workers skip the row until it
becomes due.

## Idempotent Job Middleware

```csharp
public record SendReceiptJob(
    string OrderId,
    string Email) : ChokaQBaseJob, IIdempotentJob
{
    public string IdempotencyKey => $"receipt:{OrderId}";
}
```

When idempotency middleware is enabled, ChokaQ can claim in-progress work and
store completed markers through `IIdempotencyClaimStore`. Use this for handler
side-effect protection, not just enqueue admission.

## Custom Middleware

```csharp
public sealed class AuditMiddleware : IChokaQMiddleware
{
    private readonly ILogger<AuditMiddleware> _logger;

    public AuditMiddleware(ILogger<AuditMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(IJobContext context, object? job, JobDelegate next)
    {
        _logger.LogInformation("Starting job {JobId} of type {JobType}.", context.JobId, context.JobType);
        await next();
        _logger.LogInformation("Finished job {JobId}.", context.JobId);
    }
}
```

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<BillingProfile>();
    options.AddMiddleware<AuditMiddleware>();
});
```

Middleware wraps handler execution. It does not own worker fetch, SQL leases,
circuit breaker decisions, retry scheduling, Archive, or DLQ transitions.

For the full execution model, see [Middleware Pipeline](/3-deep-dives/middleware-pipeline).

## Pipe Mode

```csharp
builder.Services.AddChokaQ(options =>
{
    options.UsePipe<GlobalPipeHandler>();
});

public sealed class GlobalPipeHandler : IChokaQPipeHandler
{
    public Task HandleAsync(string jobType, string payload, CancellationToken ct)
    {
        Console.WriteLine($"{jobType}: {payload}");
        return Task.CompletedTask;
    }
}
```

Pipe mode is useful when the application wants one handler for raw typed event
payloads. Bus mode is the normal starting point for domain jobs.

For the architecture trade-offs, see [Bus Vs Pipe Dispatch](/1-architecture/bus-vs-pipe).

## In-Memory Mode

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<BillingProfile>();
    options.ConfigureInMemory(o => o.MaxCapacity = 10_000);
});
```

In-memory mode uses process memory and `System.Threading.Channels`. It is useful
for local development and tests, but it is not durable.

## The Deck Options

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.Path = "/chokaq";
    options.AuthorizationPolicy = "ChokaQ.Read";
    options.DestructiveAuthorizationPolicy = "ChokaQ.Admin";
});
```

Use a stricter policy for destructive actions such as purge, bulk requeue, and
cancel. Anonymous dashboard access should stay limited to demos.

## Architecture Notes

These examples intentionally show the public API surface, not internal service
registration shortcuts. The goal is that an application host can install
`ChokaQ`, register profiles, select storage, map The Deck, and then reason about
runtime behavior through the documentation.

## Interview Questions

**Why does the handler still need idempotency if enqueue has an idempotency key?**  
Because enqueue dedupe prevents duplicate active rows. It does not prevent a
handler from being executed twice after a crash or retry.

**Why use semantic type keys?**  
Because SQL rows outlive CLR refactors. The persisted message contract should
not be tied to namespace or class renames.

**Why keep Bus and Pipe modes?**  
Bus mode is ergonomic for typed domain jobs. Pipe mode gives a lower-level raw
payload path for event-stream-like scenarios.
