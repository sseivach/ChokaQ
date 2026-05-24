# Примеры API

Эта страница собирает небольшие копируемые примеры для публичных API ChokaQ.
Полный walkthrough начинается в [Быстром старте](/ru/getting-started). Эту
страницу удобно использовать, когда концепция уже понятна и нужен точный shape
кода.

## Минимальный SQL Server host

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

## Типизированный job contract

```csharp
public record CapturePaymentJob(
    string PaymentId,
    decimal Amount,
    string Currency) : ChokaQBaseJob;
```

Используйте в payload стабильный business identifier. ChokaQ job ID
идентифицирует queued work item; ваш business ID идентифицирует внешнюю
операцию.

## Типизированный handler

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

Handlers должны быть безопасны для at-least-once execution. ChokaQ может
сохранять и повторять работу, но внешние side effects все равно требуют
business idempotency.

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

Type key сохраняется в SQL. Предпочитайте semantic versioned keys, а не CLR
type names, чтобы refactoring не ломал старые строки.

## Enqueue now

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

Enqueue idempotency key предотвращает duplicate active jobs с тем же business
key. Он не заменяет idempotency внутри handler.

## Enqueue с задержкой

```csharp
await queue.EnqueueAsync(
    new CapturePaymentJob(paymentId, amount, "USD"),
    queue: "billing",
    delay: TimeSpan.FromMinutes(10),
    ct: ct);
```

Delayed execution хранится как `ScheduledAtUtc`. Воркеры пропускают строку, пока
она не станет due.

## Idempotent Job Middleware

```csharp
public record SendReceiptJob(
    string OrderId,
    string Email) : ChokaQBaseJob, IIdempotentJob
{
    public string IdempotencyKey => $"receipt:{OrderId}";
}
```

Когда idempotency middleware включено, ChokaQ может claim in-progress work и
сохранять completed markers через `IIdempotencyClaimStore`. Используйте это для
защиты side effects внутри handler, а не только для admission на enqueue.

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

Middleware оборачивает handler execution. Оно не владеет worker fetch, SQL
leases, circuit breaker decisions, retry scheduling, Archive или DLQ
transitions.

Полная модель выполнения описана в [Middleware Pipeline](/ru/3-deep-dives/middleware-pipeline).

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

Pipe mode полезен, когда приложению нужен один handler для raw typed event
payloads. Bus mode - обычная стартовая точка для domain jobs.

Архитектурные trade-offs описаны в [Bus Vs Pipe Dispatch](/ru/1-architecture/bus-vs-pipe).

## In-Memory Mode

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<BillingProfile>();
    options.ConfigureInMemory(o => o.MaxCapacity = 10_000);
});
```

In-memory mode использует память процесса и `System.Threading.Channels`. Это
удобно для локальной разработки и тестов, но этот режим не durable.

## The Deck Options

```csharp
builder.Services.AddChokaQTheDeck(options =>
{
    options.Path = "/chokaq";
    options.AuthorizationPolicy = "ChokaQ.Read";
    options.DestructiveAuthorizationPolicy = "ChokaQ.Admin";
});
```

Для destructive actions, таких как purge, bulk requeue и cancel, используйте
более строгую policy. Anonymous dashboard access должен оставаться только для
demo-сценариев.

## Архитектурные заметки

Эти примеры намеренно показывают публичную API surface, а не внутренние
shortcuts регистрации сервисов. Цель в том, чтобы application host мог
установить `ChokaQ`, зарегистрировать profiles, выбрать storage, замапить The
Deck, а затем понимать runtime behavior через документацию.

## Дополнительные вопросы

**Почему handler все еще нуждается в idempotency, если у enqueue есть idempotency key?**  
Потому что enqueue dedupe предотвращает duplicate active rows. Он не мешает
handler выполниться дважды после crash или retry.

**Зачем использовать semantic type keys?**  
Потому что SQL rows живут дольше CLR refactors. Persisted message contract не
должен быть привязан к переименованию namespace или class.

**Зачем сохранять Bus и Pipe modes?**  
Bus mode удобен для типизированных domain jobs. Pipe mode дает более
низкоуровневый raw payload path для event-stream-like сценариев.
