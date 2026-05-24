# Bus Vs Pipe Dispatch

![Bus vs Pipe dispatch](/diagrams/44-bus-vs-pipe-dispatch.png)

В ChokaQ есть два dispatch modes.

| Mode | Best for | Handler shape |
|---|---|---|
| Bus | Typed domain jobs. | `IChokaQJobHandler<TJob>` |
| Pipe | Raw event-style payloads. | `IChokaQPipeHandler` |

Bus mode - default choice для application workflows. Pipe mode - более
низкоуровневый путь, когда host хочет, чтобы один handler получал много job
types как raw payloads.

## Bus Mode

Flow Bus mode:

1. Persisted type key resolve в CLR job type.
2. Payload deserializes в job DTO.
3. DI resolves `IChokaQJobHandler<TJob>`.
4. Compiled expression delegate вызывает `HandleAsync`.
5. Middleware оборачивает handler.

Bus mode дает strong typing, обычный DI и compile-time handler contracts.

## Pipe Mode

Flow Pipe mode:

1. Worker читает persisted job type string.
2. Payload остается raw.
3. DI resolves один `IChokaQPipeHandler`.
4. Middleware оборачивает pipe handler.
5. Handler получает `(jobType, payload, cancellationToken)`.

Pipe mode подходит для high-throughput routing, integration events или hosts,
которые сами владеют payload parsing.

## Shared runtime policy

Оба режима разделяют:

- durable storage;
- worker fetch;
- circuit breaker check;
- retry/DLQ policy;
- heartbeat;
- metrics;
- visibility в The Deck;
- middleware pipeline.

Меняется только dispatch shape.

## Архитектурное решение

### Почему выбран такой pattern?

Продукту нужны ergonomic typed handlers для обычных users и raw dispatch path
для advanced/high-throughput scenarios. Если оба режима остаются за одним
processor, reliability policy остается consistent.

### Trade-offs

Два режима увеличивают documentation и testing surface. Выигрыш в том, что
ChokaQ может обслуживать и domain-command workflows, и raw event processing без
fork runtime.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Bus only | Simpler mental model. | Less flexible для raw event streams. |
| Pipe only | Minimal dispatch model. | Потеря typed handler ergonomics. |
| Separate products | Strong isolation. | Duplicated runtime policy. |

### Дополнительные вопросы

**С какого mode начинать большинству apps?**  
С Bus mode, потому что typed contracts и handlers проще анализировать.

**Зачем держать Pipe mode?**  
Он поддерживает raw payload processing без необходимости регистрировать каждый
event как typed handler.

**Различаются ли reliability guarantees по mode?**  
Нет. Storage, retries, DLQ, heartbeat и circuit breakers общие.
