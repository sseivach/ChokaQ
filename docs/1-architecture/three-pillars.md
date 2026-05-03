# CHK-01: Three Pillars Architecture

## The Core Problem

Many traditional background job frameworks store **everything in a single table**:

```
| Id | Status    | Payload | CreatedAt | ... |
|----|-----------|---------|-----------|-----|
| 1  | Succeeded | ...     | Jan 1     |     |  ← Completed 6 months ago
| 2  | Succeeded | ...     | Jan 2     |     |  ← Completed 6 months ago
| .. | ...       | ...     | ...       |     |  ← 2 million more rows
| N  | Pending   | ...     | Today     |     |  ← THE ONE WE ACTUALLY NEED
```

To find that one Pending job, the database must **scan past millions of irrelevant rows**, even with indexes. Over time:
- Index fragmentation increases (constant INSERT/DELETE in the same B-tree)
- Page splits slow down writes
- `COUNT(*)` for dashboard stats becomes expensive

## The Solution: Physical Data Separation

ChokaQ splits data into **three physically separate tables**, each optimized for its specific workload:

<img src="/architecture.png" alt="Three Pillars Architecture" style="width: 100%; max-width: 900px; margin: 1.5rem auto; display: block;" />

### Pillar 1: JobsHot (The Engine Room) 🔵

The high-concurrency workhorse. Only contains jobs that are **actively in play**.

| Column | Purpose |
|--------|---------|
| `Status` | `0:Pending`, `1:Fetched`, `2:Processing` — only three states |
| `Priority` | Descending sort — higher number = processed first |
| `HeartbeatUtc` | Updated every N seconds during processing — zombie detection |
| `ScheduledAtUtc` | For delayed/retry scheduling — `NULL` means "run now" |
| `IdempotencyKey` | Unique filtered index — prevents duplicate enqueuing |

**Key Optimization:** The fetch index is **filtered** — it only contains `Pending` jobs:

```sql
CREATE NONCLUSTERED INDEX [IX_JobsHot_Fetch]
ON [chokaq].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc], [CreatedAtUtc])
INCLUDE ([Id], [Type])
WHERE [Status] = 0                    -- 👈 Only Pending jobs in the index
WITH (DATA_COMPRESSION = PAGE, FILLFACTOR = 80);
```

This means the index is always **tiny** — even if thousands of jobs are processing, the fetch index only tracks pending ones.
`CreatedAtUtc` is part of the key because delayed jobs and immediate jobs share the same fetch path; the engine orders by effective schedule time and uses creation time as the stable tie-breaker.

### Pillar 2: JobsArchive (The Success Vault) 🟢

Write-once, read-many. Once a job succeeds, it's atomically moved here.

| Column | Purpose |
|--------|---------|
| `DurationMs` | Execution time for performance analytics |
| `FinishedAtUtc` | Completion timestamp for trend charts |
| `AttemptCount` | How many tries it took |

**Key Optimization:** `PAGE` compression (since data is rarely updated):
```sql
CONSTRAINT [PK_JobsArchive] PRIMARY KEY CLUSTERED ([Id] ASC)
WITH (DATA_COMPRESSION = PAGE)
```

SQL Server PAGE compression can achieve **50-70% storage reduction** on text-heavy rows.

### Pillar 3: JobsDLQ — The Morgue 🔴

Jobs that **died**. Each has a detailed cause of death:

```csharp
public enum FailureReason
{
    MaxRetriesExceeded = 0,  // Exhausted all retry attempts
    Cancelled = 1,           // Admin cancelled via The Deck
    Zombie = 2,              // Heartbeat expired — worker crashed
    CircuitBreakerOpen = 3,  // Too many failures for this job type
    Rejected = 4,            // Validation failure on enqueue
    Throttled = 5,           // Downstream rate limit or overload signal
    FatalError = 6,          // Poison-pill failure that should not retry
    Timeout = 7,             // Handler exceeded its execution timeout
    Transient = 8            // Retryable failure family after exhaustion
}
```

DLQ supports:
- **Filtering by reason** — "show me all zombies from last week"
- **Payload editing** — fix broken JSON directly in The Deck
- **Resurrection** — move back to Hot table with reset `AttemptCount`

### Pillar 4: StatsSummary (The Speedometer) 📊

Pre-aggregated counters for **O(1) dashboard reads**:

```sql
CREATE TABLE [chokaq].[StatsSummary](
    [Queue]           VARCHAR(255) NOT NULL,
    [SucceededTotal]  BIGINT NOT NULL DEFAULT 0,
    [FailedTotal]     BIGINT NOT NULL DEFAULT 0,
    [RetriedTotal]    BIGINT NOT NULL DEFAULT 0,
    [LastActivityUtc] DATETIME2(7) NULL
);
```

Instead of `SELECT COUNT(*) FROM JobsArchive` (expensive scan), the dashboard reads a single pre-computed row.

## Atomic Transitions: The Safety Net

Every movement between pillars is **atomic** — a single SQL batch that either fully succeeds or fully fails.

### Success Path: Hot → Archive

```sql
-- Delete from Hot and capture the row via OUTPUT
DELETE FROM [chokaq].[JobsHot]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type], DELETED.[Payload],
    DELETED.[Tags], DELETED.[AttemptCount], DELETED.[WorkerId],
    DELETED.[CreatedBy], NULL,
    DELETED.[CreatedAtUtc], DELETED.[StartedAtUtc],
    SYSUTCDATETIME(), @DurationMs
INTO [chokaq].[JobsArchive](...)
WHERE [Id] = @JobId;

-- Atomically increment the success counter
MERGE [chokaq].[StatsSummary] AS target
USING (SELECT @Queue AS Queue) AS source
ON target.[Queue] = source.[Queue]
WHEN MATCHED THEN
    UPDATE SET SucceededTotal = SucceededTotal + 1,
               LastActivityUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Queue, SucceededTotal, ...) VALUES (@Queue, 1, ...);
```

::: danger 🔑 Critical Design Decision
The important pattern is not "copy in application code, then delete later." ChokaQ performs the move inside one SQL transaction and uses `OUTPUT` to capture the exact row being moved. That keeps the state transition atomic and gives worker-ownership guards a single place to decide whether the move is still valid.
:::

### Failure Path: Hot → DLQ

Same pattern, but with `FailureReason` and `ErrorDetails`:

```sql
DELETE FROM [chokaq].[JobsHot]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type], DELETED.[Payload],
    DELETED.[Tags], @FailureReason, @ErrorDetails, DELETED.[AttemptCount],
    DELETED.[WorkerId], DELETED.[CreatedBy], NULL,
    DELETED.[CreatedAtUtc], SYSUTCDATETIME()
INTO [chokaq].[JobsDLQ](...)
WHERE [Id] = @JobId;
```

### Resurrection Path: DLQ → Hot

The reverse — gives the job a second chance with reset state:

```sql
DELETE FROM [chokaq].[JobsDLQ]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type],
    CASE WHEN @NewPayload IS NOT NULL THEN @NewPayload ELSE DELETED.[Payload] END,
    CASE WHEN @NewTags IS NOT NULL THEN @NewTags ELSE DELETED.[Tags] END,
    NULL,   -- IdempotencyKey reset
    ISNULL(@NewPriority, 10),
    0,      -- Status = Pending
    0,      -- AttemptCount reset to 0
    ...
INTO [chokaq].[JobsHot](...)
WHERE [Id] = @JobId;
```

::: tip 💡 Architecture Insight
Updating a status column instead of moving the row means the fetch query must always filter out completed rows. With physical separation, the fetch index only contains **active** jobs — eliminating wasted I/O and maintaining a tiny index footprint.
:::

## Performance Impact

| Metric | Single-Table Design | Three Pillars |
|--------|-------------------|---------------|
| Fetch query scan | All rows (millions) | Only Pending rows |
| Index fragmentation | High (mixed INSERT/DELETE) | Low (append-mostly per table) |
| Archive query speed | Competes with active queries | Dedicated index, PAGE compressed |
| Dashboard stats | `COUNT(*)` full scan | O(1) read from StatsSummary |
| Storage efficiency | Uncompressed hot data | PAGE compression on cold data |

<br>

> *Next: Learn [Why SQL Server?](/1-architecture/why-sql-server) — the database-level features that make this architecture possible.*
