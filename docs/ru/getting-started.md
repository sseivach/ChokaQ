# Быстрый старт

Эта страница помогает запустить host с ChokaQ и объясняет, за что отвечает
каждая часть.

Если вы впервые смотрите на ChokaQ, сначала прочитайте [Обзор](/ru/overview).
Если хотите сразу что-то запустить, используйте Docker sample ниже.

## Требования

- **.NET 10 SDK**
- **SQL Server** для persistent mode, либо In-Memory mode для разработки

## Минимальная полезная ментальная модель

Перед кодом полезно увидеть форму системы:

| Часть | Что вы добавляете | Что она делает |
|---|---|---|
| Job DTO | Класс или record, который реализует `IChokaQJob`. | Описывает работу, которую нужно выполнить. |
| Handler | `IChokaQJobHandler<TJob>`. | Выполняет вашу бизнес-логику. |
| Profile | `ChokaQJobProfile`. | Связывает job type keys с обработчиками. |
| Storage | SQL Server или in-memory. | Хранит активную работу и финальные состояния. |
| Worker | Регистрируется ChokaQ. | Забирает задания и запускает обработчики. |
| The Deck | `/chokaq`. | Показывает задания, очереди, health, ошибки и управляющие действия. |

Обычный путь выглядит так:

```text
enqueue -> JobsHot -> worker claim -> handler -> Archive or DLQ
```

Для delayed jobs и retries строка остается в `JobsHot` с будущим
`ScheduledAtUtc`. Воркеры игнорируют ее, пока задание не станет due.

## Запуск SQL sample через Docker

В репозитории есть Docker Compose sample, который вместе поднимает SQL Server и
Bus sample app:

```powershell
docker compose up --build
```

Затем откройте:

```text
http://localhost:5299
http://localhost:5299/chokaq
http://localhost:5299/health
```

Полный walkthrough находится в [Docker Compose Sample](/ru/samples/docker-compose).

## Package Reference

Потребительский install target - верхнеуровневый пакет `ChokaQ`. Он транзитивно
подтягивает runtime subpackages (`ChokaQ.Core`, `ChokaQ.Storage.SqlServer`,
`ChokaQ.TheDeck` и `ChokaQ.Abstractions`), поэтому application host не должен
ссылаться на каждый внутренний пакет отдельно.

```xml
<ItemGroup>
    <PackageReference Include="ChokaQ" Version="0.1.0-preview.1" />
</ItemGroup>
```

Для проверки package-consumer path и preview trial используйте
`samples/ChokaQ.Sample.NuGetLab`: он восстанавливает `ChokaQ` через NuGet, а не
через source project references. Для source development внутри этого репозитория
Bus и Pipe samples пока используют project references.

## Минимальная настройка: Bus Mode + SQL Server

Bus mode означает, что у каждого задания есть типизированный DTO и
типизированный обработчик. Это лучший стартовый режим для обычной прикладной
работы.

### 1. Настройте `Program.cs`

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

### 2. Настройте `appsettings.json`

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

Полный справочник конфигурации находится в [Runtime Configuration](/ru/configuration).
ChokaQ валидирует опасные значения на старте, поэтому плохие timeouts, пустые
SQL connection strings или неположительные cleanup batch sizes падают до того,
как воркеры смогут забрать production jobs.

Перед запуском работы с side effects прочитайте
[Delivery Guarantees](/ru/delivery-guarantees). ChokaQ дает at-least-once
execution, а не exactly-once для внешних side effects. Обработчики, которые
отправляют email, списывают деньги, вызывают API, публикуют messages или
обновляют другие системы, должны использовать стабильные idempotency keys,
unique constraints, outbox patterns или idempotency, поддерживаемую провайдером.

`/health` - обычный ASP.NET Core health-check endpoint. В SQL Server mode он
показывает доступность SQL schema, liveness воркеров и saturation очередей. В
production держите endpoint защищенным или доступным только внутри доверенной
инфраструктуры, если health payloads могут быть видны снаружи.

OpenTelemetry metrics используют meter `ChokaQ`. `ChokaQ:Metrics` ограничивает
cardinality tags, чтобы случайные динамические queue names, job types, errors
или DLQ reasons схлопывались в `other`, а не создавали неограниченное число
monitoring time series.

The Deck безопасен по умолчанию. `AddChokaQTheDeck()` мапит UI и SignalR Hub с
обязательной авторизацией, если вы явно не выставили `AllowAnonymousDeck = true`
для локального demo или публичного sandbox.

::: tip Auto-Provisioning
При `AutoCreateSqlTable = true` ChokaQ автоматически создаст schema `chokaq`,
все runtime tables (`JobsHot`, `JobsArchive`, `JobsDLQ`, `StatsSummary`,
`MetricBuckets`, `Queues`), ledger `SchemaMigrations` и оптимизированные indexes
при первом запуске. Операция идемпотентна: ее безопасно выполнять на каждом
restart.
:::

### 3. Опишите Job и Handler

Каждый типизированный ChokaQ job - это DTO, который реализует `IChokaQJob`.
Поддерживаются две формы:

- наследоваться от `ChokaQBaseJob`, если нужен компактный immutable contract с
  автоматически сгенерированным `Id`;
- реализовать `IChokaQJob` напрямую, если нужен обычный class, mutable
  properties или собственная генерация ID.

`ChokaQBaseJob` сам является C# `record`, поэтому любой DTO, который от него
наследуется, тоже должен быть `record`. Это правило языка C#, а не runtime
ограничение ChokaQ. Не пишите `class MyJob : ChokaQBaseJob`: компилятор
отклонит такой код. Если нужен class, реализуйте `IChokaQJob` напрямую.

```csharp
// Job DTO (the contract). ChokaQBaseJob is a record, so this DTO is a record.
public record SendEmailJob(string To, string Subject) : ChokaQBaseJob;
```

Унаследованный `Id` генерируется при создании job object. В positional record
constructor кладите только бизнес-поля.

Эквивалентный class-based contract:

```csharp
public sealed class SendEmailJob : IChokaQJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}
```

Используйте class form, если существующее приложение уже использует mutable
DTOs, если serializer или mapper ожидает parameterless constructor, или если
job ID должен приходить из вашей correlation/idempotency scheme. Handlers,
queueing, storage, retries и The Deck работают одинаково для обеих форм.

Подробнее о serialization, type-key versioning и idempotency keys:
[Job Contracts](/ru/job-contracts).

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

### 4. Зарегистрируйте в Profile

Profiles группируют связанные jobs. `typeKey` - это строковый идентификатор,
который хранится в базе данных; он отвязывает CLR type name от persisted
contract.

```csharp
public class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
    }
}
```

`typeKey` сохраняется вместе с заданием. Предпочитайте стабильные semantic keys,
например `email.send.v1`, а не CLR class names: refactoring не должен менять
persisted message contract.

### 5. Поставьте задание в очередь

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

## In-Memory Mode без SQL Server

Для разработки и тестов можно полностью пропустить `UseSqlServer()`:

```csharp
builder.Services.AddChokaQ(options =>
{
    options.AddProfile<MailingProfile>();
    options.ConfigureInMemory(o => o.MaxCapacity = 10_000);
});
```

Jobs хранятся в памяти процесса и обрабатываются через
`System.Threading.Channels`. База данных не нужна, но этот режим не durable.
Если процесс завершается, in-memory jobs и history теряются. Для production
работы, которая должна переживать restart, используйте SQL Server mode.

## Pipe Mode для high-throughput

Для raw event streaming без типизированных handlers:

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

## Добавление Middleware

Каждое выполнение job можно перехватить middleware в стиле ASP.NET Core:

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

Middleware выполняется вокруг handler dispatch. Это правильное место для
logging, correlation, audit tags, validation, timing и idempotency guards. Оно
не владеет fetch, получением worker lease, circuit breaker decisions, retry
scheduling, Archive или DLQ transitions: это остаются lifecycle policies
processor/storage.

Middleware выполняется вокруг handler в порядке регистрации. В Bus mode
аргумент `job` - десериализованный DTO. В Pipe mode это raw payload string.

## Что проверить после запуска

Откройте `/chokaq` и посмотрите на эти сигналы:

| Сигнал | Значение |
|---|---|
| Pending | Jobs ждут воркера. |
| Buffered или Fetched | Jobs забраны воркерами, но пользовательский код еще не выполняется. |
| Processing | Handlers выполняются прямо сейчас. |
| Succeeded | Jobs перенесены в Archive. |
| Failed или DLQ | Jobs требуют разбора или repair. |
| Queue lag | Как долго eligible work ждет выполнения. |

Затем откройте `/health`. В SQL Server mode health checks проверяют доступ к
SQL schema, liveness воркеров и saturation очередей.

## Что дальше

| Тема | Что вы узнаете |
|-------|-------------------|
| [Обзор](/ru/overview) | Короткую модель продукта и архитектуры. |
| [Why ChokaQ?](/ru/why-chokaq) | Основную философию и архитектурные преимущества. |
| [Delivery Guarantees](/ru/delivery-guarantees) | Что обещает ChokaQ и что должны учитывать обработчики. |
| [Package Topology](/ru/1-architecture/package-topology) | Почему установка `ChokaQ` подтягивает runtime subpackages. |
| [Bus Vs Pipe Dispatch](/ru/1-architecture/bus-vs-pipe) | Чем отличаются typed jobs и raw pipe events. |
| [Queue Registry And Profiles](/ru/1-architecture/queue-registry-and-profiles) | Как связаны profiles, type keys, handlers и queues. |
| [Three Pillars](/ru/1-architecture/three-pillars) | Зачем физически разделять Hot, Archive и DLQ. |
| [SQL Schema Atlas](/ru/3-deep-dives/sql-schema-atlas) | Для чего нужна каждая SQL table и каждый index. |
| [SQL Query Reference](/ru/3-deep-dives/sql-query-reference) | Как важные runtime queries забирают, перемещают, восстанавливают и наблюдают jobs. |
| [Transaction Integrity](/ru/3-deep-dives/transaction-integrity) | Почему lifecycle moves атомарны, но external side effects все равно требуют idempotency. |
| [Prefetching](/ru/3-deep-dives/prefetching) | Как SQL fetching отделен от handler execution. |
| [Serialization And Envelope Limits](/ru/3-deep-dives/serialization-and-envelope-limits) | Как enforcing применяется к payloads, type keys, tags и size limits. |
| [Middleware Pipeline](/ru/3-deep-dives/middleware-pipeline) | Чем middleware может и не может владеть. |
| [State Machine](/ru/2-lifecycle/state-machine) | Полный lifecycle job от Pending до Archive. |
| [Retry And DLQ](/ru/2-lifecycle/retry-and-dlq) | Как работают retries, terminal failures и resurrection. |
| [Production Readiness](/ru/5-operations/production-readiness-checklist) | Checklist для production rollout. |
| [The Deck](/ru/4-the-deck/realtime-signalr) | Real-time admin dashboard на SignalR. |
