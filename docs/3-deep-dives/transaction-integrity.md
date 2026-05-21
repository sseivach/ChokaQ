# Transaction Integrity

![Transaction integrity across Hot, Archive, and DLQ](/diagrams/22-transaction-integrity-hot-archive-dlq.png)

Transaction integrity is the line between "a job moved" and "a job was copied
somewhere and maybe deleted later." ChokaQ treats final lifecycle transitions as
database integrity boundaries.

The important moves are:

- `JobsHot` to `JobsArchive` after success;
- `JobsHot` to `JobsDLQ` after terminal failure, cancellation, or zombie rescue;
- `JobsDLQ` back to `JobsHot` during resurrection;
- final-state counter and metric bucket updates that must match the move.

## Where It Lives

The SQL templates live in `ChokaQ.Storage.SqlServer.DataEngine.Queries`.
The storage API executes them through `SqlJobStorage`. Worker-owned finalization
is routed through `JobProcessor` and `JobStateManager`.

## The Move Pattern

The core pattern is:

1. `SET XACT_ABORT ON`.
2. Begin a short SQL transaction.
3. Delete the source row.
4. Capture the exact deleted row with `OUTPUT ... INTO @Moved`.
5. Insert the captured row into the destination table.
6. Update `StatsSummary`.
7. Update `MetricBuckets` when the move represents a completion outcome.
8. Commit.
9. Roll back on error.

That means the job is never half-moved. If the destination insert or counter
update fails, SQL Server rolls the source row back.

## Why Delete First?

For Archive and DLQ moves, deleting from `JobsHot` first captures the
authoritative row. The row captured in `@Moved` is exactly the row that was
removed. This matters when stale workers, zombie rescue, cancellation, or admin
actions race each other.

For resurrection, deleting from `JobsDLQ` first prevents two admin tabs from
inserting the same job back into `JobsHot`.

## Ownership Guards

Worker-owned transitions include `WorkerId` predicates where applicable. A
stale worker should not be able to finalize a job after another recovery path
has already reclaimed or moved it.

Zero affected rows are not treated as successful movement. They mean the
runtime no longer owns that row.

## What Transactions Do Not Solve

Database transactions protect ChokaQ state. They do not make external side
effects exactly-once.

If a handler charges a card and the process crashes before Archive, ChokaQ may
run the job again after recovery. The fix is business idempotency at the handler
or downstream provider boundary.

## Architecture Decision

### Why this pattern?

The database is already the durable source of truth. Using short transactions
for lifecycle moves keeps correctness local to the storage provider and avoids
introducing a second coordination system.

### Trade-offs

Transactions add lock duration and require careful query design. ChokaQ keeps
transactions short and limited to state movement, counters, and metric bucket
writes. User handler code never runs inside these SQL transactions.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Copy then delete in application code | Simple to write. | Can duplicate or lose rows on crash. |
| Single-table status update | Fewer cross-table moves. | Hot path competes with historical data. |
| Distributed transaction with external side effect | Stronger theoretical coupling. | Operationally fragile and rarely supported by modern services. |

### When not to use this approach

For extremely high-throughput ephemeral events where audit/history is
unimportant, a broker append log may be a better fit. ChokaQ optimizes for
durable business work, visibility, and repairability.

### Additional Questions

**Which operations must be atomic?**  
Final state transitions: Hot to Archive, Hot to DLQ, DLQ to Hot, and their
associated counters/metric buckets.

**What happens after handler success but before Archive if the process crashes?**  
The job may be recovered and executed again. That is why ChokaQ documents
at-least-once execution and requires idempotent side effects.

**Why not put handler execution inside the SQL transaction?**  
Because external calls can be slow, block locks, time out, or never participate
in the database transaction. The transaction should protect ChokaQ state, not
wrap arbitrary business work.

