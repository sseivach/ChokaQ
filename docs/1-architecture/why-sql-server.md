# Why SQL Server?

## The Deliberate Choice

ChokaQ uses SQL Server not because "it's all we know" — but because SQL Server provides **database-level primitives** that are impossible to replicate in application code or with generic key-value stores.

## The Four Killer Features

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

**Result:** 50 workers can execute this query simultaneously. Each gets a unique set of rows. Zero deadlocks. Zero duplicates.

::: warning 🎯 Why Not Redis?
Redis uses `RPOPLPUSH` for competing consumers, which only supports FIFO ordering. ChokaQ supports **priority-based** ordering (`ORDER BY Priority DESC`), scheduled execution, and multi-column filtering — all in a single atomic query. Try doing that with Redis lists.
:::

### 2. OUTPUT Clause: Atomic Cross-Table Moves

SQL Server's `OUTPUT` clause allows deleting from one table and inserting into another **in a single atomic operation**:

```sql
DELETE FROM [chokaq].[JobsHot]
OUTPUT DELETED.* INTO [chokaq].[JobsArchive](...)
WHERE [Id] = @JobId;
```

No transactions, no two-phase commit, no "gap" where the job exists in neither table. The row is either in Hot or in Archive — never in limbo.

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
ON [chokaq].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc])
INCLUDE ([Id], [Type])
WHERE [Status] = 0
```

**Why this matters:**
- If 10,000 jobs are processing and 50 are pending → the index has **50 entries**, not 10,050
- Index maintenance cost is proportional to pending count, not total row count
- B-tree stays shallow → consistent O(log n) lookup time

## Why Not Other Databases?

| Database | Missing Feature | Impact |
|----------|----------------|--------|
| **PostgreSQL** | No `READPAST` equivalent | Must use `SKIP LOCKED` (similar but different semantics) |
| **MySQL** | No filtered indexes | Cannot optimize fetch index for Pending-only rows |
| **SQLite** | No row-level locking | Single-writer model — no concurrent fetching |
| **MongoDB** | No ACID cross-collection moves | Cannot atomically move doc between collections |
| **Redis** | No complex ordering | Only FIFO, no priority + scheduling + filtering |

::: tip 💡 PostgreSQL Support?
`SKIP LOCKED` in PostgreSQL is functionally equivalent to `READPAST`. A future `ChokaQ.Storage.PostgreSQL` package is architecturally possible — the `IJobStorage` abstraction is database-agnostic. The SQL queries would need adaptation, but the Three Pillars architecture would work identically.
:::

## The Transaction Advantage

SQL Server transactions give us guarantees that application-level locking cannot:

1. **Crash Safety:** If the process crashes mid-operation, uncommitted changes are automatically rolled back
2. **Isolation:** `UPDLOCK` survives connection pooling and async context switches
3. **Durability:** Once committed, data survives power failures (WAL + checkpoints)
4. **Deadlock Detection:** SQL Server's lock manager automatically detects and resolves deadlocks (though `READPAST` prevents most)

Compare this to Redis-based queues where a crash between `RPOPLPUSH` and `ACK` can lose messages, or MongoDB where cross-collection atomicity requires distributed transactions.

<br>

> *Next: See how we eliminated every third-party dependency in [Zero-Dependency Philosophy](/1-architecture/zero-dependency).*
