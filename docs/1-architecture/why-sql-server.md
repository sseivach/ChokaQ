# Why SQL Server?

## The Deliberate Choice

ChokaQ uses SQL Server because it provides database-level primitives that match
the product goal: durable job state, transactional lifecycle transitions,
operator inspection, and concurrent worker coordination inside the same storage
boundary.

## The Four Key Features

### 1. Row-Level Locking: `UPDLOCK + READPAST`

The foundation of our competing consumer pattern:

```sql
SELECT TOP (@Limit) h.*
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
WHERE h.[Status] = 0
ORDER BY h.[Priority] DESC, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

| Hint | What It Does |
|------|-------------|
| `UPDLOCK` | Acquires an update lock on each selected row — no other transaction can grab it |
| `READPAST` | Skips rows already locked by other workers — no blocking, no waiting |

**Result:** many workers can execute this query simultaneously, and each committed fetch gets a unique set of claimed rows. The pattern reduces blocking and prevents duplicate claims on the fetch path; SQL Server can still detect and resolve deadlocks in unrelated transactions, so ChokaQ also treats deadlocks as transient SQL failures.

::: warning Design Boundary
Key-value and list-based queues are excellent for simple enqueue/dequeue
workflows. ChokaQ needs priority ordering, delayed execution, queue filtering,
worker ownership, and lifecycle inspection in one durable model, so the first
provider uses SQL Server rather than a list-oriented backend.
:::

### 2. OUTPUT Clause: Atomic Cross-Table Moves

SQL Server's `OUTPUT` clause allows ChokaQ to capture rows while they are deleted or updated. ChokaQ wraps cross-table lifecycle moves in short transactions so the state transition and its counters commit together:

```sql
DELETE FROM [chokaq].[JobsHot]
OUTPUT DELETED.* INTO [chokaq].[JobsArchive](...)
WHERE [Id] = @JobId;
```

No distributed transaction, no two-phase commit, and no application-side "copy then delete" gap. The row is either in Hot or in Archive after the transaction commits.

### 3. PAGE Compression

Archive and DLQ tables use `DATA_COMPRESSION = PAGE`:

```sql
CONSTRAINT [PK_JobsArchive] PRIMARY KEY CLUSTERED ([Id] ASC)
WITH (DATA_COMPRESSION = PAGE)
```

PAGE compression uses:
- **Column-prefix compression** — stores common prefixes once
- **Dictionary compression** — replaces repeated values with tokens

For text-heavy job payloads (JSON), this achieves **50-70% storage reduction** with minimal CPU overhead on reads.

### 4. Filtered Indexes

The fetch index only covers `Status = 0` (Pending):

```sql
CREATE NONCLUSTERED INDEX [IX_JobsHot_Fetch]
ON [chokaq].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc], [CreatedAtUtc])
INCLUDE ([Id], [Type])
WHERE [Status] = 0
```

**Why this matters:**
- If 10,000 jobs are processing and 50 are pending → the index has **50 entries**, not 10,050
- Index maintenance cost is proportional to pending count, not total row count
- B-tree stays shallow → consistent O(log n) lookup time
- `CreatedAtUtc` matches the fetch tie-breaker, avoiding avoidable sort/lookups on the hottest path

## Backend Trade-Offs

| Backend | Trade-off for ChokaQ's current provider |
|----------|----------------|--------|
| **PostgreSQL** | Similar competing-consumer behavior is possible with `SKIP LOCKED`, but it needs a separate provider and test matrix. |
| **MySQL** | Provider support would need a different indexing and locking design. |
| **SQLite** | Useful for embedded scenarios, but the single-writer model does not match ChokaQ's concurrent worker target. |
| **MongoDB** | A document provider would need a separate lifecycle and transaction design. |
| **Redis** | Strong fit for fast queue primitives, but ChokaQ's first provider prioritizes relational inspection and transactional state moves. |

::: tip 💡 PostgreSQL Support?
`SKIP LOCKED` in PostgreSQL solves a similar competing-consumer problem. A future PostgreSQL provider is architecturally possible because the `IJobStorage` abstraction is database-facing. It should become a separate package only when it is real production support, not while SQL Server is the only provider.
:::

## The Transaction Advantage

SQL Server transactions give us guarantees that application-level locking cannot:

1. **Crash Safety:** If the process crashes mid-operation, uncommitted changes are automatically rolled back
2. **Isolation:** `UPDLOCK` survives connection pooling and async context switches
3. **Durability:** Once committed, data survives power failures (WAL + checkpoints)
4. **Deadlock Detection:** SQL Server's lock manager automatically detects and resolves deadlocks (though `READPAST` prevents most)

This is the storage contract ChokaQ optimizes around. Other backends can be
valid choices, but each one would need an equivalent provider-specific answer
for claiming work, recording final state, and recovering after process failure.

## Read Consistency Policy

ChokaQ does not treat `NOLOCK` as a general performance trick. Dirty reads are allowed only for passive dashboard telemetry where the UI is already an approximate snapshot:

- summary counters.
- queue saturation health.
- recent throughput and failure-rate windows.
- top DLQ error groups.

Correctness and operator-decision paths use committed reads or explicit locking instead:

- fetch and bulkhead capacity decisions.
- worker ownership and state transitions.
- DLQ bulk previews.
- job inspectors and history pages.
- queue management rows.

This split is intentional. Dashboards should observe the system without becoming the workload. But anything that can cause execution, purge, requeue, edit, pause, or capacity admission must avoid uncommitted data.

## Performance Baseline Tests

ChokaQ keeps SQL performance honest with integration tests against a real SQL Server container. These tests are guardrails, not microbenchmarks:

- `FetchNextBatchAsync` is measured against a mixed Hot table with pending, fetched, and processing rows.
- `GetSystemHealthAsync` is measured against a dashboard-sized operational snapshot across Hot, Archive, and DLQ.
- Archive and DLQ history paging are measured with committed reads and thousands of rows.

The budgets are intentionally generous because Docker and CI runners are noisy. The tests are designed to catch query-shape regressions, missing-index mistakes, and accidental unbounded scans, not to claim an exact latency number for every production database. Real deployments should still use their own Query Store, wait-stat, and index-usage telemetry, but the repository now has a repeatable floor that protects the most important SQL paths.

<br>

> *Next: See how ChokaQ keeps infrastructure dependencies small and explicit in [Minimal Dependency Philosophy](/1-architecture/minimal-dependencies).*
