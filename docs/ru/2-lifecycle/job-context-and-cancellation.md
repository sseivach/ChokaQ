# Job Context And Cancellation

![Job context and cancellation](/diagrams/45-job-context-cancellation.png)

`IJobContext` дает handlers доступ к execution context, не раскрывая worker internals.

Handlers могут:

- прочитать текущий `JobId`;
- наблюдать текущий execution `CancellationToken`;
- отправлять progress в The Deck.

## Context surface

```csharp
public interface IJobContext
{
    string JobId { get; }
    CancellationToken CancellationToken { get; }
    Task ReportProgressAsync(int percentage);
}
```

## Cancellation sources

Execution token может быть отменен из нескольких источников:

| Source | Meaning |
|---|---|
| Handler timeout | Execution превысил настроенный runtime budget. |
| Admin cancellation | Operator запросил stop из The Deck или worker API. |
| Worker shutdown | Host останавливается, execution должен завершиться или отмениться. |

Cancellation является cooperative. Handlers должны передавать token в downstream async calls и проверять его в long-running loops.

## Progress reporting

`ReportProgressAsync` ограничивает progress диапазоном `0..100` и уведомляет The Deck через runtime notifier. Progress - это UI signal, а не state transition.

## Developer guidance

Хорошие handlers:

- передают `CancellationToken` в database, HTTP, storage и SDK calls;
- не проглатывают `OperationCanceledException`;
- отправляют progress только на meaningful boundaries;
- держат external side effects idempotent;
- не предполагают, что cancellation означает rollback в downstream.

## Архитектурное решение

### Почему этот pattern?

Handler'у нужен execution context, но он не должен знать о workers, SignalR, SQL leases или cancellation registries. `IJobContext` сохраняет public surface маленьким.

### Trade-offs

Context намеренно ограничен. Он не раскрывает mutable job state или storage APIs, потому что это позволило бы handlers обходить lifecycle policy.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Передавать worker object в handlers | Максимальная сила. | Ломает encapsulation и lifecycle safety. |
| Ambient static context | Удобно. | Сложнее tests и больше hidden coupling. |
| No context | Просто. | Нет progress reporting и job-local cancellation surface. |

### Дополнительные вопросы

**Cancellation гарантированно останавливает side effects?**  
Нет. Это cooperative механизм. Downstream systems могли уже принять work.

**Почему не exposing storage из context?**  
Потому что handlers не должны выполнять lifecycle transitions напрямую.

**Что handler должен делать с token?**  
Передавать его во все cancellable async operations и корректно останавливаться по запросу.
