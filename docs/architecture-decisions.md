# Architecture Decisions

![Architecture decision map](/diagrams/61-architecture-decision-map.png)

This page is the decision index for ChokaQ. It explains the major choices behind
the runtime and points to the deep dives that defend each choice.

## Decision Map

| Decision | Why it matters | Deep dive |
|---|---|---|
| SQL Server as the durable backend | Keeps accepted work restart-safe and inspectable. | [Why SQL Server?](/1-architecture/why-sql-server) |
| Three Pillars schema | Keeps active work separate from history and recovery evidence. | [Three Pillars](/1-architecture/three-pillars) |
| At-least-once execution | Honest contract for durable work with external side effects. | [Delivery Guarantees](/delivery-guarantees) |
| Atomic Hot/Archive/DLQ moves | Prevents half-moved jobs. | [Transaction Integrity](/3-deep-dives/transaction-integrity) |
| `UPDLOCK + READPAST` fetch | Lets many workers claim work without central locks. | [SQL Concurrency](/3-deep-dives/sql-concurrency) |
| Bounded prefetch | Keeps SQL as backlog and process memory bounded. | [Bounded Prefetch](/3-deep-dives/bounded-prefetch) |
| Retry + circuit breaker | Separates per-job retry from systemic failure protection. | [Retry And DLQ](/2-lifecycle/retry-and-dlq), [Circuit Breakers](/4-the-deck/circuit-breakers) |
| DLQ instead of silent drop | Preserves failed work for repair and audit. | [Failure Taxonomy](/2-lifecycle/failure-taxonomy) |
| Heartbeat + zombie rescue | Distinguishes healthy long work from dead owners. | [Heartbeat](/2-lifecycle/heartbeat) |
| The Deck as an operator surface | Makes repair workflows explicit and safer than ad hoc SQL. | [Panel Guide](/4-the-deck/panel-guide) |

## Decision Quality Bar

Every major decision should be defendable with:

- the problem it solves;
- the operational consequence;
- the trade-off;
- the alternative that was rejected;
- the failure mode that still remains.

## Interview Questions

**What is the single most important design choice?**  
Using SQL as the durable coordination boundary. Everything else builds from
that: fetch, retry, DLQ, observability, and operations.

**What boundary does ChokaQ choose?**  
ChokaQ is a SQL-backed background job engine with strong operational visibility.
It is not designed as a streaming platform, broker mesh, or distributed log.

**Where is the hardest correctness boundary?**  
Between handler side effects and job finalization. ChokaQ protects its own
state with transactions, but external side effects require idempotent handler
design.
