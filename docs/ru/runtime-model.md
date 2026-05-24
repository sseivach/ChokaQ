# Runtime Model

![ChokaQ global system context](/diagrams/00-global-system-context.png)

Эта страница дает самую короткую ментальную модель ChokaQ. Ее стоит прочитать до deep dives.

ChokaQ - не отдельный queue server, который потом вызывает ваше приложение по сети. Это .NET package, который работает внутри процесса вашего приложения. Приложение ставит jobs в очередь, ChokaQ сохраняет их в SQL Server, а hosted worker ChokaQ позже вызывает ваши зарегистрированные handlers через dependency injection.

## Общая схема

```text
Процесс вашего приложения
├─ API, UI, controllers, services
├─ IChokaQQueue.EnqueueAsync(...)
├─ ChokaQ hosted worker
├─ Ваши job handlers
└─ The Deck dashboard

SQL Server
├─ JobsHot
├─ JobsArchive
├─ JobsDLQ
├─ Queues
└─ MetricBuckets
```

Приложение владеет business code. ChokaQ владеет delivery, storage transitions, retry policy, heartbeat, DLQ, metrics и operator visibility.

## End-to-end flow

1. Application code создает job DTO.
2. Application code вызывает `IChokaQQueue.EnqueueAsync(...)`.
3. ChokaQ получает type key job из `ChokaQJobProfile`.
4. ChokaQ сериализует job payload.
5. ChokaQ вставляет `Pending` row в `JobsHot`.
6. ChokaQ worker poll'ит SQL Server и claim'ит eligible rows.
7. Worker переводит row в `Fetched` и кладет ее в bounded local buffer.
8. Processor забирает row, validates ownership и переводит ее в `Processing`.
9. ChokaQ deserializes payload и resolves ваш handler из DI.
10. ChokaQ вызывает ваш handler.
11. При success job moves to `JobsArchive`.
12. При retryable failure job reschedule'ится в `JobsHot`.
13. При terminal failure, cancellation или zombie detection job moves to `JobsDLQ`.
14. The Deck, health checks, metrics и logs показывают результат.

## Пример workload

Представим, что одно приложение должно:

- отправить 1,000 emails;
- отправить 50,000 messages;
- создать 300 тяжелых Excel reports, каждый примерно по пять минут.

Приложение не передает в ChokaQ указатель на метод. Оно сохраняет durable job messages:

```csharp
await queue.EnqueueAsync(new SendEmailJob(customerId, templateId));
await queue.EnqueueAsync(new SendMessageJob(channelId, body));
await queue.EnqueueAsync(new CreateExcelReportJob(reportId, fromUtc, toUtc));
```

У каждого message есть registered handler:

```csharp
CreateJob<SendEmailJob, SendEmailHandler>("email.send.v1");
CreateJob<SendMessageJob, SendMessageHandler>("message.send.v1");
CreateJob<CreateExcelReportJob, CreateExcelReportHandler>("report.excel.create.v1");
```

ChokaQ не знает, как отправлять email или строить Excel files. Это знают ваши handlers. ChokaQ знает, как сохранить work, claim'ить ее один раз, вызвать правильный handler, safely retry, isolate queues и сохранить failures для inspection.

## Queue isolation

Разные workloads обычно стоит класть в разные queues:

```text
emails      -> fast, external provider limited
messages    -> high volume, throughput sensitive
reports     -> slow, CPU/IO heavy
```

Queue-level controls не дают slow work занять все execution slots:

```json
{
  "ChokaQ": {
    "Queues": {
      "emails": { "MaxWorkers": 20 },
      "messages": { "MaxWorkers": 50 },
      "reports": {
        "MaxWorkers": 3,
        "ZombieTimeoutSeconds": 2400
      }
    }
  }
}
```

С такой настройкой 300 пятиминутных report jobs остаются durable backlog в SQL. Они не забивают process memory и не обязаны блокировать email или message work.

## Несколько инстансов приложения

Если вы запускаете три instance одного приложения, все три могут запускать ChokaQ workers против одной SQL Server database:

```text
App instance A ┐
App instance B ├─ SQL Server
App instance C ┘
```

Workers координируются через SQL row locks и ownership predicates. Один job claim'ится одним worker. Horizontal scaling увеличивает processing capacity, пока bottleneck не станет SQL Server или downstream dependency.

## Что происходит при failure

ChokaQ - at-least-once execution engine. Это значит, что handler может выполниться больше одного раза после crashes или recovery. Handlers должны быть idempotent, если они создают external side effects.

Типичные outcomes:

| Situation | ChokaQ behavior |
|---|---|
| Handler succeeds | Move from `JobsHot` to `JobsArchive`. |
| Transient dependency failure | Reschedule in `JobsHot` with retry delay and jitter. |
| Fatal payload or code error | Move to `JobsDLQ` with failure details. |
| Worker crashes before user code starts | Return stale `Fetched` work to `Pending`. |
| Worker crashes during handler execution | Heartbeat expires; move job to DLQ as `Zombie`. |
| Operator fixes a DLQ payload | Resurrect row back to `JobsHot` for normal execution. |

## Куда идти дальше

| Что нужно понять | Читать |
|---|---|
| Как зарегистрировать jobs и handlers | [Getting Started](/ru/getting-started) |
| Как должен выглядеть job contract | [Job Contracts](/ru/job-contracts) |
| Полная state machine | [State Machine](/ru/2-lifecycle/state-machine) |
| Как SQL workers безопасно claim rows | [SQL Concurrency](/ru/3-deep-dives/sql-concurrency) |
| Как вызываются handlers | [Expression Trees](/ru/3-deep-dives/expression-trees) |
| Как failures retry'ятся или уходят в DLQ | [Retry And DLQ](/ru/2-lifecycle/retry-and-dlq) |
| Как эксплуатировать систему | [Operations Runbooks](/ru/5-operations/runbooks) |
