# Job Contracts

Эта страница объясняет, как проектировать типизированные jobs в ChokaQ.

Простыми словами, job contract - это сообщение, которое вы кладете в фоновую
систему. В нем должны быть данные, нужные для выполнения работы позже. В нем не
должно быть live services, database contexts, HTTP request objects или чего-то,
что имеет смысл только внутри текущего request.

Типизированный job - это message contract, который ChokaQ serializes, stores,
retries, показывает в The Deck и передает вашему handler. В терминах кода это
любой DTO, который реализует `IChokaQJob`.

```csharp
public interface IChokaQJob
{
    string Id { get; }
}
```

`Id` идентифицирует один физический job instance. ChokaQ использует его для
tracking, logging, state transitions, retries, archive/DLQ rows и dashboard
links.

## Правило для старта

Кладите в job факты, а не поведение.

Хорошие поля job:

- `OrderId`
- `UserId`
- `Email`
- `WebhookUrl`
- `ReportDate`
- `PaymentAttemptId`

Плохие поля job:

- `DbContext`
- `HttpContext`
- service clients
- open streams
- delegates или lambdas
- request cancellation tokens

Когда handler запустится, он может загрузить свежее состояние из вашей базы
данных. Это безопаснее, чем сериализовать большой object graph, который может
устареть к моменту обработки job.

## Две поддерживаемые формы

ChokaQ поддерживает record-based и class-based job DTOs.

Используйте `ChokaQBaseJob`, когда нужен компактный immutable DTO и
автоматическая генерация job ID:

```csharp
using ChokaQ.Abstractions.Jobs;

public sealed record SendEmailJob(string To, string Subject) : ChokaQBaseJob;
```

Используйте `IChokaQJob` напрямую, когда нужен обычный class, mutable
properties, parameterless construction или собственная генерация job ID:

```csharp
using ChokaQ.Abstractions.Jobs;

public sealed class SendEmailJob : IChokaQJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}
```

Обе формы являются first-class. Handlers, queueing, SQL storage, retries, The
Deck и type-key registration работают одинаково для обеих.

## Почему `ChokaQBaseJob` требует record

`ChokaQBaseJob` объявлен как abstract C# `record`:

```csharp
public abstract record ChokaQBaseJob : IChokaQJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}
```

C# разрешает records наследоваться только от records. Значит, это валидно:

```csharp
public sealed record SendEmailJob(string To) : ChokaQBaseJob;
```

А это невалидно:

```csharp
public sealed class SendEmailJob(string To) : ChokaQBaseJob; // compiler error
```

Если DTO должен быть class, реализуйте `IChokaQJob` напрямую.

## Выбор record vs class

Используйте `record : ChokaQBaseJob`, когда:

- job в основном является immutable data;
- нужна самая короткая декларация DTO;
- generated value equality приемлема для app code;
- default generated `Id` достаточно;
- job можно создать через positional constructor или `init` properties.

Используйте `class : IChokaQJob`, когда:

- существующее приложение уже использует class DTOs;
- serializer, mapper или model binder ожидает parameterless constructor;
- нужны mutable `set` properties;
- `Id` нужно генерировать из собственной correlation scheme;
- вы не хотите record equality semantics в message type.

## Регистрация handler

Handler и profile не зависят от того, какую форму DTO вы выбрали.

```csharp
using ChokaQ.Abstractions.Jobs;

public sealed class EmailHandler : IChokaQJobHandler<SendEmailJob>
{
    public Task HandleAsync(SendEmailJob job, CancellationToken ct)
    {
        // Send email here.
        return Task.CompletedTask;
    }
}

public sealed class MailingProfile : ChokaQJobProfile
{
    public MailingProfile()
    {
        CreateJob<SendEmailJob, EmailHandler>("email.send.v1");
    }
}
```

`typeKey` - durable name, который хранится вместе с job. Предпочитайте
стабильные semantic keys, например `email.send.v1`, а не CLR type names. Если
позже вы переименуете `SendEmailJob`, но payload contract останется тем же,
type key может остаться стабильным.

Обычно у одного DTO type должен быть один активный enqueue key. ChokaQ позволяет
зарегистрировать один CLR type под несколькими keys для migration scenarios, но
reverse lookup от type к key сохраняет первый зарегистрированный key. Это
значит, что `queue.EnqueueAsync(new SendEmailJob(...))` использует первый key
для этого type. Если нужно активно enqueue и `email.send.v1`, и
`email.send.v2`, лучше использовать отдельные DTO types или явный migration
path, а не полагаться на один CLR type с двумя active keys.

## Enqueue examples

Record-based DTO:

```csharp
await queue.EnqueueAsync(
    new SendEmailJob("user@example.com", "Welcome"),
    priority: 10);
```

Class-based DTO:

```csharp
await queue.EnqueueAsync(
    new SendEmailJob
    {
        To = "user@example.com",
        Subject = "Welcome"
    },
    priority: 10);
```

## Рекомендации по serialization

Относитесь к job DTOs как к persisted message contracts, а не как к приватной
детали реализации.

- Держите payload fields сериализуемыми через `System.Text.Json`.
- Предпочитайте primitive, string, enum, `DateTimeOffset`, `Guid` и небольшие
  nested DTO fields вместо service objects или database entities.
- Не кладите `DbContext`, service clients, delegates, streams, cancellation
  tokens или request-scoped framework objects внутрь job.
- По возможности добавляйте новые optional fields с defaults.
- Избегайте rename или remove fields, пока old jobs могут существовать в Hot,
  Archive или DLQ.
- Если payload shape меняется несовместимо, регистрируйте новый type key,
  например `email.send.v2`.
- Держите DTO сфокусированным на данных, которые нужны для фоновой операции.
  Большой или быстро меняющийся domain state загружайте в handler по ID.

## Job ID vs Idempotency Key

`IChokaQJob.Id` - physical job instance ID. Он должен быть уникальным для
каждого enqueue.

Idempotency key - отдельный business key для duplicate prevention. Используйте
его, когда несколько enqueue attempts представляют одну и ту же logical
operation.

Ключ можно передать явно при enqueue:

```csharp
await queue.EnqueueAsync(
    new SendEmailJob("user@example.com", "Welcome"),
    idempotencyKey: "welcome-email:user@example.com");
```

Или положить его в job contract через `IIdempotentJob`:

```csharp
using ChokaQ.Abstractions.Idempotency;
using ChokaQ.Abstractions.Jobs;

public sealed record SendEmailJob(string UserId, string Email)
    : ChokaQBaseJob, IIdempotentJob
{
    public string IdempotencyKey => $"welcome-email:{UserId}";
    public TimeSpan? ResultTtl => TimeSpan.FromHours(24);
}
```

Не используйте `Id` как idempotency key, если только вы намеренно не хотите,
чтобы каждый enqueue был уникальным. Для duplicate prevention idempotency key
должен быть deterministic для logical operation.
