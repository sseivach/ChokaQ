# Job Contracts

This page explains how to design typed ChokaQ jobs.

In plain language, a job contract is the message you put into the background
system. It should contain the data needed to perform the work later. It should
not contain live services, database contexts, HTTP request objects, or anything
that only makes sense inside the current request.

A typed job is the message contract that ChokaQ serializes, stores, retries,
shows in The Deck, and passes to your handler. In code terms, it is any DTO that
implements `IChokaQJob`.

```csharp
public interface IChokaQJob
{
    string Id { get; }
}
```

The `Id` identifies one physical job instance. ChokaQ uses it for tracking,
logging, state transitions, retries, archive/DLQ rows, and dashboard links.

## Beginner Rule

Put facts in the job, not behavior.

Good job fields:

- `OrderId`
- `UserId`
- `Email`
- `WebhookUrl`
- `ReportDate`
- `PaymentAttemptId`

Bad job fields:

- `DbContext`
- `HttpContext`
- service clients
- open streams
- delegates or lambdas
- request cancellation tokens

The handler can load fresh state from your database when it runs. That is safer
than serializing a large object graph that may be stale by the time the job is
processed.

## Two Supported Shapes

ChokaQ supports both record-based and class-based job DTOs.

Use `ChokaQBaseJob` when you want a compact immutable DTO and automatic job ID
generation:

```csharp
using ChokaQ.Abstractions.Jobs;

public sealed record SendEmailJob(string To, string Subject) : ChokaQBaseJob;
```

Use `IChokaQJob` directly when you want an ordinary class, mutable properties,
parameterless construction, or custom job ID generation:

```csharp
using ChokaQ.Abstractions.Jobs;

public sealed class SendEmailJob : IChokaQJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}
```

Both forms are first-class. Handlers, queueing, SQL storage, retries, The Deck,
and type-key registration work the same way for both.

## Why `ChokaQBaseJob` Requires a Record

`ChokaQBaseJob` is declared as an abstract C# `record`:

```csharp
public abstract record ChokaQBaseJob : IChokaQJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
}
```

C# only allows records to inherit from records. That means this is valid:

```csharp
public sealed record SendEmailJob(string To) : ChokaQBaseJob;
```

This is not valid:

```csharp
public sealed class SendEmailJob(string To) : ChokaQBaseJob; // compiler error
```

If your DTO needs to be a class, implement `IChokaQJob` directly.

## Choosing Record vs Class

Use a `record : ChokaQBaseJob` when:

- the job is mostly immutable data;
- you want the shortest DTO declaration;
- generated value equality is acceptable for your app code;
- the default generated `Id` is enough;
- your job can be constructed with a positional constructor or `init`
  properties.

Use a `class : IChokaQJob` when:

- your existing application already uses class DTOs;
- your serializer, mapper, or model binder expects a parameterless constructor;
- you want mutable `set` properties;
- you want to generate `Id` from your own correlation scheme;
- you want to keep record equality semantics out of the message type.

## Handler Registration

The handler and profile do not care which DTO shape you choose.

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

The `typeKey` is the durable name stored with the job. Prefer stable semantic
keys such as `email.send.v1` instead of CLR type names. If you rename
`SendEmailJob` later but keep the same payload contract, the type key can stay
stable.

Normally, one DTO type should have one active enqueue key. ChokaQ allows the
same CLR type to be registered under multiple keys for migration scenarios, but
reverse lookup from type to key keeps the first registered key. That means
`queue.EnqueueAsync(new SendEmailJob(...))` uses the first key for that type. If
you need to actively enqueue both `email.send.v1` and `email.send.v2`, prefer
separate DTO types or an explicit migration path instead of relying on one CLR
type with two active keys.

## Enqueue Examples

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

## Serialization Guidance

Treat job DTOs as persisted message contracts, not as private implementation
details.

- Keep payload fields serializable by `System.Text.Json`.
- Prefer primitive, string, enum, `DateTimeOffset`, `Guid`, and small nested DTO
  fields over service objects or database entities.
- Do not put `DbContext`, service clients, delegates, streams, cancellation
  tokens, or request-scoped framework objects inside the job.
- Add new optional fields with defaults when possible.
- Avoid renaming or removing fields while old jobs may still exist in Hot,
  Archive, or DLQ.
- If the payload shape changes incompatibly, register a new type key such as
  `email.send.v2`.
- Keep the DTO focused on data required to perform the background operation.
  Load large or fast-changing domain state inside the handler by ID.

## Job ID vs Idempotency Key

`IChokaQJob.Id` is the physical job instance ID. It should be unique per enqueue.

An idempotency key is a separate business key for duplicate prevention. Use it
when multiple enqueue attempts represent the same logical operation.

You can provide the key explicitly at enqueue time:

```csharp
await queue.EnqueueAsync(
    new SendEmailJob("user@example.com", "Welcome"),
    idempotencyKey: "welcome-email:user@example.com");
```

Or put it on the job contract with `IIdempotentJob`:

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

Do not reuse `Id` as the idempotency key unless you intentionally want every
enqueue to be unique. For duplicate prevention, the idempotency key must be
deterministic for the logical operation.
