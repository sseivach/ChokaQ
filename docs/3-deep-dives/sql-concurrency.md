# SQL Concurrency: UPDLOCK + READPAST

## The Challenge: Competing Consumers

Multiple worker instances (or threads) need to grab jobs from the **same table simultaneously** without:
- Two workers grabbing the **same job** (duplicate processing)
- Workers **blocking** each other (deadlocks, waits)
- **Lost updates** when concurrent UPDATEs collide

This is the classic **competing consumers** problem in distributed systems.

## The Solution: A Single SQL Query

```sql
-- From: Queries.cs — FetchNextBatch

WITH ActiveCounts AS (
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [{SCHEMA}].[JobsHot]
    WHERE [Status] IN (1, 2)
    GROUP BY [Queue]
),
Candidates AS (
    SELECT
        h.[Id],
        h.[Priority],
        ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) AS SortUtc,
        q.[MaxWorkers],
        ISNULL(ac.CurrentActive, 0) AS CurrentActive,
        ROW_NUMBER() OVER (
            PARTITION BY h.[Queue]
            ORDER BY h.[Priority] DESC,
                     ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
        ) AS QueueRank
    FROM [{SCHEMA}].[JobsHot] h WITH (UPDLOCK, READPAST)
    LEFT JOIN [{SCHEMA}].[Queues] q ON h.[Queue] = q.[Name]
    LEFT JOIN ActiveCounts ac ON h.[Queue] = ac.[Queue]
    WHERE h.[Status] = 0
      AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)
      AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)
      AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
),
Picked AS (
    SELECT TOP (@Limit) [Id]
    FROM Candidates
    WHERE [MaxWorkers] IS NULL
       OR ([CurrentActive] + [QueueRank]) <= [MaxWorkers]
    ORDER BY [Priority] DESC, [SortUtc] ASC
)
UPDATE h
SET h.[Status] = 1,                    -- Fetched
    h.[WorkerId] = @WorkerId,
    h.[LastUpdatedUtc] = SYSUTCDATETIME()
OUTPUT
    INSERTED.[Id], INSERTED.[Queue], INSERTED.[Type],
    INSERTED.[Payload], INSERTED.[Tags], INSERTED.[IdempotencyKey],
    INSERTED.[Priority], INSERTED.[Status], INSERTED.[AttemptCount],
    INSERTED.[WorkerId], INSERTED.[HeartbeatUtc],
    INSERTED.[ScheduledAtUtc], INSERTED.[CreatedAtUtc],
    INSERTED.[StartedAtUtc], INSERTED.[LastUpdatedUtc],
    INSERTED.[CreatedBy], INSERTED.[LastModifiedBy]
FROM [{SCHEMA}].[JobsHot] h WITH (UPDLOCK, READPAST)
INNER JOIN Picked p ON p.[Id] = h.[Id]
```

Let's break this apart piece by piece.

## The Locking Hints Explained

### `UPDLOCK` — Reserve Before Update

```sql
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, ...)
```

When SQL Server reads a row with `UPDLOCK`, it acquires an **update lock** (U-lock) on that row:
- Other readers (normal `SELECT`) can still read the row ✅
- Other `UPDLOCK` readers **skip** the row or **wait** ❌
- No other transaction can update or delete the locked row ❌

The U-lock is held until the transaction completes — in our case, until the `UPDATE SET Status = 1` finishes.

### `READPAST` — Skip Locked Rows

```sql
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
```

Without `READPAST`, Worker B would **block** waiting for Worker A to release its locks. With `READPAST`:

```
Worker A: SELECT with UPDLOCK → grabs rows 1, 2, 3 (locks them)
Worker B: SELECT with UPDLOCK, READPAST → SKIPS 1,2,3 → grabs rows 4, 5, 6
Worker C: SELECT with UPDLOCK, READPAST → SKIPS 1-6 → grabs rows 7, 8, 9
```

The hot path avoids waiting on rows that another worker already locked. That is
the operational reason for `READPAST`: workers keep making progress instead of
forming a convoy behind the first locked row.

### Committed ActiveCounts

```sql
WITH ActiveCounts AS (
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [chokaq].[JobsHot]
    WHERE [Status] IN (1, 2)
    GROUP BY [Queue]
)
```

The CTE that counts active jobs per queue deliberately uses the default committed-read behavior.
This count participates in the bulkhead decision, so dirty reads are not acceptable:

- A dirty low count could let a queue temporarily exceed `MaxWorkers`.
- A dirty high count could starve a queue based on work that later rolls back.
- Capacity decisions belong to the correctness path; only passive dashboard telemetry is allowed to use `NOLOCK`.

This is still not a serializable global semaphore. The authoritative protection remains the
single `UPDATE ... OUTPUT` fetch statement plus the worker-owned `MarkAsProcessing` gate, but
reading committed active counts avoids avoidable decisions based on uncommitted data.

## The UPDATE...OUTPUT Pattern

Instead of `SELECT` + `UPDATE` (two round-trips, race condition window), ChokaQ uses a single `UPDATE...OUTPUT`:

```sql
UPDATE TOP (@Limit) h
SET h.[Status] = 1,
    h.[WorkerId] = @WorkerId,
    h.[LastUpdatedUtc] = SYSUTCDATETIME()
OUTPUT INSERTED.*                     -- Return the updated rows
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
WHERE ...
```

**Why this is superior to SELECT + UPDATE:**

| Approach | Round-trips | Race Window | Locks |
|----------|------------|-------------|-------|
| `SELECT` then `UPDATE` | 2 | Gap between SELECT and UPDATE | Must hold lock across two operations |
| `UPDATE...OUTPUT` | 1 | None — atomic | Lock acquired and released in one operation |

## Walking Through The Candidate Filters

```sql
WHERE h.[Status] = 0                                   -- Only Pending
  AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)       -- Not paused
  AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)       -- Not deactivated
  AND (h.[ScheduledAtUtc] IS NULL                      -- Not scheduled for future
       OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())

-- Bulkhead check happens after candidates are ranked per queue:
WHERE [MaxWorkers] IS NULL
   OR ([CurrentActive] + [QueueRank]) <= [MaxWorkers]
```

| # | Filter | Purpose |
|---|--------|---------|
| 1 | `Status = 0` | Only grab Pending jobs, not Fetched or Processing. |
| 2 | `IsPaused = 0` | Respect queue pause. |
| 3 | `IsActive = 1` | Skip deactivated queues. |
| 4 | `ScheduledAtUtc <= NOW` | Only fetch jobs whose delay has expired. |
| 5 | `CurrentActive + QueueRank <= MaxWorkers` | Enforce remaining per-queue capacity inside the batch, so one fetch cannot overshoot a queue limit. |

## The ORDER BY: Priority + Schedule

```sql
ORDER BY h.[Priority] DESC,
         ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

1. **Priority DESC** — Higher number = processed first. A `Priority = 100` job always runs before `Priority = 10`
2. **ScheduledAt ASC** — Within the same priority, oldest-scheduled jobs run first (FIFO within priority class)

The `ISNULL` handles the common case where `ScheduledAtUtc` is NULL (immediate execution) — falls back to creation time for FIFO ordering.

## Concurrency Proof

**Scenario:** 3 workers, batch size 2, 9 pending jobs

```
Database state BEFORE fetch:

| Row 1 | Row 2 | Row 3 |
|---|---|---|
| Job-1 (P) | Job-2 (P) | Job-3 (P) |
| Job-4 (P) | Job-5 (P) | Job-6 (P) |
| Job-7 (P) | Job-8 (P) | Job-9 (P) |


Worker A executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → Locks Job-1, Job-2
  → Sets Status=1, WorkerId="worker-A"
  → OUTPUT returns Job-1, Job-2

Worker B executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → READPAST skips Job-1, Job-2 (locked by A)
  → Locks Job-3, Job-4
  → Sets Status=1, WorkerId="worker-B"
  → OUTPUT returns Job-3, Job-4

Worker C executes: UPDATE TOP(2) ... WITH (UPDLOCK, READPAST)
  → READPAST skips Job-1–4 (locked by A, B)
  → Locks Job-5, Job-6
  → OUTPUT returns Job-5, Job-6

Result: each worker gets a unique pair from this fetch path.
```

::: tip 💡 Architecture Insight
The `UPDLOCK + READPAST` combination gives ChokaQ a database-level claim
primitive. It prevents duplicate fetch claims without a distributed lock service.
Processing still uses worker ownership and lease checks because real systems
must also handle shutdown, stale buffers, zombie rescue, and operator actions
after the initial fetch.
:::

<br>

> *Next: See how [Expression Trees](/3-deep-dives/expression-trees) eliminate reflection overhead for handler invocation.*
