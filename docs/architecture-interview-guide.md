# Architecture Interview Guide

![Interview system design map](/diagrams/62-interview-system-design-map.png)

This guide prepares ChokaQ for a senior architecture review or system-design
interview. The goal is not to memorize implementation trivia. The goal is to
show that each important design has a reason, a cost, and a recovery story.

## The One-Minute Pitch

ChokaQ is a .NET background job engine backed by SQL Server. It stores active
jobs in `JobsHot`, successful jobs in `JobsArchive`, and failed/operator-held
jobs in `JobsDLQ`. Workers claim rows with SQL locking, process them through a
typed or raw dispatcher, and finalize state through atomic SQL transitions. The
Deck provides real-time operational inspection and repair workflows.

## Questions A Staff Interviewer Will Ask

### Why SQL Server?

Because the product optimizes for durable business jobs, inspectability, and
operator repair. SQL gives transactional state, familiar backup/restore,
queryable evidence, and a simple deployment story for teams already running SQL.

Trade-off: SQL becomes the central throughput boundary. If the workload needs
massive streaming throughput, a broker/log may be better.

### Why not exactly-once?

Exactly-once external side effects are not something a queue engine can promise
for arbitrary handlers. ChokaQ promises at-least-once execution and provides
tools for idempotency, DLQ, heartbeat, and recovery.

### Why Hot/Archive/DLQ instead of one table?

Active fetch should not compete with years of history. The split keeps the hot
worker path small while preserving audit and repair data.

### What happens on worker crash?

Fetched jobs that did not start user code can return to pending. Processing jobs
with expired heartbeat move to DLQ as zombies because side effects may already
exist.

### Why have The Deck?

A durable queue without an operator surface pushes recovery into ad hoc SQL.
The Deck turns repair into authenticated, bounded, observable workflows.

## What To Admit Honestly

- SQL will not beat a dedicated broker for huge streaming workloads.
- At-least-once requires handler idempotency.
- Dashboard actions need serious authorization.
- Prefetching adds a stale-ownership window that must be guarded.
- Retention and DLQ policy are operational responsibilities.

## Strong Close

ChokaQ is production-oriented because it does not hide failure. It stores state,
classifies failure, exposes operations, and forces dangerous actions through
explicit control points.

