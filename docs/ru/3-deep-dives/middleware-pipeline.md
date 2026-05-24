# Middleware Pipeline

![Middleware pipeline](/diagrams/43-middleware-pipeline.png)

Middleware ChokaQ оборачивает выполнение handler'а cross-cutting поведением. По духу это похоже на ASP.NET Core middleware, но scope ограничен job execution.

Middleware не отвечает за fetching, SQL leases, retry scheduling, circuit breaker decisions, Archive или DLQ. Это остается runtime lifecycle policy.

## Где это находится

Public contract - `IChokaQMiddleware`. Dispatchers собирают pipeline:

- `BusJobDispatcher` передает deserialized job DTO;
- `PipeJobDispatcher` передает raw payload string.

Middleware выполняется вокруг core handler delegate.

## Execution order

Middleware регистрируются в application configuration и оборачиваются вокруг handler'а в заданном порядке. Middleware может:

- log;
- добавить correlation;
- audit;
- validate;
- измерить execution time;
- enforce idempotency;
- остановиться до вызова `next()`.

## Что middleware не должно делать

Middleware не должно:

- напрямую менять ChokaQ storage state;
- перемещать jobs в Archive или DLQ;
- обходить retry policy;
- silently swallow cancellation;
- выполнять long blocking work перед handler'ом;
- скрывать failures, которые должны быть видны processor'у.

Если middleware throws, processor трактует это как execution failure и применяет обычную retry/DLQ policy.

## Архитектурное решение

### Почему этот pattern?

Middleware дает applications единый hook для cross-cutting execution concerns, не заставляя каждый handler повторять один и тот же boilerplate.

### Trade-offs

Middleware добавляет ordering complexity. Logging middleware, idempotency middleware и validation middleware могут вести себя по-разному в зависимости от order, поэтому registration должен быть explicit и documented.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Handler base class | Простое inheritance. | Ломает composition и multiple concerns. |
| Attributes only | Declarative. | Сложнее dependency injection и ordering. |
| No middleware | Меньше runtime. | Повторяющийся handler boilerplate. |

### Дополнительные вопросы

**Почему не дать middleware владеть final state transitions?**  
Потому что lifecycle correctness должен оставаться централизованным на boundary processor/storage.

**Что будет, если middleware упадет?**  
Job execution fail'ится как handler failure и проходит через retry/DLQ policy.

**Почему в Pipe mode передается raw payload?**  
Pipe mode намеренно раскрывает raw event stream shape одному global handler'у.
