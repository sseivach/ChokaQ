# Queue Registry And Job Profiles

![Queue registry and profiles](/diagrams/46-queue-registry-profiles.png)

Profiles - compile-time registration surface для typed jobs. Queues - runtime
partition и operational control surface.

Они решают разные задачи:

| Concept | Purpose |
|---|---|
| Job profile | Мапит persisted type keys на DTOs и handlers. |
| Queue | Группирует runtime work для throttling, pause, lag и worker budgets. |

## Job Profiles

Profile регистрирует job contracts:

```csharp
public sealed class BillingProfile : ChokaQJobProfile
{
    public BillingProfile()
    {
        CreateJob<CapturePaymentJob, CapturePaymentHandler>("billing.capture-payment.v1");
    }
}
```

Profile заполняет type registry на startup. Duplicate type keys fail fast.

## Queues

Queues хранятся в SQL и могут создаваться автоматически при enqueue jobs.
Таблица `Queues` хранит runtime configuration:

- `IsPaused`;
- `IsActive`;
- `ZombieTimeoutSeconds`;
- `MaxWorkers`;
- `LastUpdatedUtc`.

Workers читают queue configuration во время fetch и processing.

## Relationship

Один job type может выполняться в разных queues в зависимости от enqueue.
Type key управляет dispatch. Queue управляет operational behavior.

Пример:

- `email.send.v1` в `transactional-email`;
- `email.send.v1` в `marketing-email`;
- один handler contract;
- разные queue lag, pause и worker caps.

## Архитектурное решение

### Почему выбран такой pattern?

Dispatch identity и operational partitioning - разные concerns. Если их
объединить, каждое изменение queue станет изменением type-contract.

### Trade-offs

Operators должны понимать, что queue names не равны job types. Documentation и
The Deck должны показывать оба понятия.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| One queue per type key | Simple mapping. | Слишком rigid для operational grouping. |
| Queue owns handler mapping | Operationally direct. | Dispatch начинает зависеть от runtime queue config. |
| No named queues | Simpler storage. | Нет per-queue throttling или pause. |

### Дополнительные вопросы

**Зачем разделять type key и queue?**  
Type key - message contract. Queue - operational partition.

**Может ли один job type быть в нескольких queues?**  
Да, если application enqueues его таким образом.

**Что должно быть globally unique?**  
Type keys в profiles. Queue names - operational labels.
