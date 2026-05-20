# Alternatives Analysis

![Alternatives analysis](/diagrams/63-alternatives-analysis.png)

ChokaQ chooses SQL Server as the first durable backend. That is a product
decision, not a claim that SQL is the best queue for every workload.

## Comparison

| Backend | Strength | Weakness | When it is better |
|---|---|---|---|
| SQL Server | Durable, transactional, inspectable, easy for SQL shops. | Central DB bottleneck. | Business jobs, operational repair, moderate/high reliability needs. |
| RabbitMQ | Mature broker, routing, acknowledgements. | Separate infrastructure and management model. | Message routing and broker-native workflows. |
| Kafka | Massive throughput, replayable log. | Operational complexity and different semantics. | Event streaming, analytics, replay-heavy systems. |
| SQS | Managed queue, simple scale. | Cloud-specific and less queryable. | AWS-first systems that want managed infrastructure. |
| PostgreSQL | Strong relational backend. | Provider work and different lock semantics. | Teams standardized on PostgreSQL. |
| In-memory channels | Very fast and simple. | Not durable. | Tests, demos, disposable local work. |

## Why SQL First?

SQL Server gives ChokaQ a compact production story:

- jobs are rows;
- state is queryable;
- final transitions are transactional;
- operators can inspect the same data the runtime uses;
- samples can run with Docker;
- many enterprise .NET teams already operate SQL Server.

## Why Not Broker First?

A broker-first product would need a separate durable history/repair store to
support The Deck, DLQ editing, history paging, failure grouping, and SQL-like
inspection. SQL makes that state native.

## Architecture Decision

The first backend is SQL Server because ChokaQ optimizes for inspectable,
transactional background jobs in .NET systems, not for maximum broker throughput
or event-stream replay. That decision aligns storage, state transitions,
operator UI, and repair workflows around one durable relational model.

This is not a universal claim. Kafka, RabbitMQ, SQS, PostgreSQL, and in-memory
channels all have contexts where they are better choices. The documentation
therefore frames SQL Server as the deliberate product default for this release,
not as the only possible backend forever.

The extension path is provider-oriented: another backend must reproduce the
same lifecycle semantics, not merely accept and deliver messages.

## Interview Questions

**When would Kafka be better?**  
When the core requirement is high-volume ordered event streaming and replay, not
operator-managed background jobs.

**When would RabbitMQ be better?**  
When routing, exchange semantics, and broker-native acknowledgements are the
center of the system.

**What would a PostgreSQL provider require?**  
A provider package with equivalent schema, fetch semantics, lock behavior,
transactions, health checks, and migration scripts.
