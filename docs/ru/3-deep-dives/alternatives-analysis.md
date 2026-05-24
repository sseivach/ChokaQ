# Alternatives Analysis

![Alternatives analysis](/diagrams/63-alternatives-analysis.png)

ChokaQ выбирает SQL Server как первый durable backend. Это product decision, а не утверждение, что SQL - лучшая queue для каждого workload.

## Comparison

| Backend | Strength | Trade-off | Best fit |
|---|---|---|---|
| SQL Server | Durable, transactional, inspectable, easy for SQL shops. | Central DB bottleneck. | Business jobs, operational repair, moderate/high reliability needs. |
| RabbitMQ | Mature broker, routing, acknowledgements. | Separate infrastructure and management model. | Message routing and broker-native workflows. |
| Kafka | Massive throughput, replayable log. | Operational complexity and different semantics. | Event streaming, analytics, replay-heavy systems. |
| SQS | Managed queue, simple scale. | Cloud-specific and less queryable. | AWS-first systems that want managed infrastructure. |
| PostgreSQL | Strong relational backend. | Provider work and different lock semantics. | Teams standardized on PostgreSQL. |
| In-memory channels | Very fast and simple. | Not durable. | Tests, demos, disposable local work. |

## Почему SQL first?

SQL Server дает ChokaQ compact production story:

- jobs - это rows;
- state queryable;
- final transitions transactional;
- operators могут inspect'ить те же data, которые использует runtime;
- samples можно запускать с Docker;
- многие enterprise .NET teams уже эксплуатируют SQL Server.

## Почему SQL before broker backends?

Broker-first product потребовал бы отдельный durable history/repair store для поддержки The Deck, DLQ editing, history paging, failure grouping и SQL-like inspection. SQL делает это state native.

## Архитектурное решение

Первый backend - SQL Server, потому что ChokaQ оптимизируется под inspectable, transactional background jobs в .NET systems, а не под maximum broker throughput или event-stream replay. Это решение выравнивает storage, state transitions, operator UI и repair workflows вокруг одной durable relational model.

Это не universal claim. Kafka, RabbitMQ, SQS, PostgreSQL и in-memory channels имеют contexts, где они подходят лучше. Поэтому documentation описывает SQL Server как deliberate product default для этого release, а не как единственный возможный backend навсегда.

Extension path ориентирован на providers: другой backend должен воспроизвести те же lifecycle semantics, а не просто accept и deliver messages.

## Дополнительные вопросы

**Когда Kafka подходит лучше?**  
Когда core requirement - high-volume ordered event streaming и replay, а не operator-managed background jobs.

**Когда RabbitMQ подходит лучше?**  
Когда routing, exchange semantics и broker-native acknowledgements находятся в центре системы.

**Что потребует PostgreSQL provider?**  
Provider package с equivalent schema, fetch semantics, lock behavior, transactions, health checks и migration scripts.
