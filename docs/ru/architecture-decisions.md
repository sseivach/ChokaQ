# Architecture Decisions

![Architecture decision map](/diagrams/61-architecture-decision-map.png)

Эта страница - decision index для ChokaQ. Она объясняет основные выборы runtime
и ведет к deep dives, где защищается каждое решение.

## Decision map

| Decision | Why it matters | Deep dive |
|---|---|---|
| SQL Server как durable backend | Accepted work остается restart-safe и inspectable. | [Почему SQL Server?](/ru/1-architecture/why-sql-server) |
| Three Pillars schema | Active work отделена от history и recovery evidence. | [Three Pillars](/ru/1-architecture/three-pillars) |
| At-least-once execution | Честный contract для durable work с external side effects. | [Delivery Guarantees](/ru/delivery-guarantees) |
| Atomic Hot/Archive/DLQ moves | Предотвращает half-moved jobs. | [Transaction Integrity](/ru/3-deep-dives/transaction-integrity) |
| `UPDLOCK + READPAST` fetch | Позволяет многим workers claim work без central locks. | [SQL Concurrency](/ru/3-deep-dives/sql-concurrency) |
| Bounded prefetch | Оставляет SQL backlog, а process memory bounded. | [Bounded Prefetch](/ru/3-deep-dives/bounded-prefetch) |
| Retry + circuit breaker | Разделяет per-job retry и systemic failure protection. | [Retry And DLQ](/ru/2-lifecycle/retry-and-dlq), [Circuit Breakers](/ru/4-the-deck/circuit-breakers) |
| DLQ instead of silent drop | Сохраняет failed work для repair и audit. | [Failure Taxonomy](/ru/2-lifecycle/failure-taxonomy) |
| Heartbeat + zombie rescue | Отличает healthy long work от dead owners. | [Heartbeat](/ru/2-lifecycle/heartbeat) |
| The Deck как operator surface | Делает repair workflows explicit и безопаснее ad hoc SQL. | [Panel Guide](/ru/4-the-deck/panel-guide) |

## Decision quality bar

Каждое крупное решение должно быть defendable через:

- problem, который оно решает;
- operational consequence;
- trade-off;
- rejected alternative;
- failure mode, который все еще остается.

## Дополнительные вопросы

**Какое design choice самое важное?**  
Использование SQL как durable coordination boundary. Из этого вырастают fetch,
retry, DLQ, observability и operations.

**Какую boundary выбирает ChokaQ?**  
ChokaQ - SQL-backed background job engine с сильной operational visibility. Он
не проектируется как streaming platform, broker mesh или distributed log.

**Где самая сложная correctness boundary?**  
Между handler side effects и job finalization. ChokaQ защищает собственное state
transactions, но external side effects требуют idempotent handler design.
