# CHK-03: Bulkhead Isolation

## The Noisy Neighbor Problem

Imagine two queues in your system:

| Queue | Task | Duration | Volume |
|-------|------|----------|--------|
| `pdf-generation` | Generate 200-page reports | 30–120 seconds | 50/hour |
| `sms-notifications` | Send SMS via API | 200ms | 10,000/hour |

Without isolation, a flood of 50 PDF jobs will **consume all worker slots**. The 10,000 SMS notifications sit in the queue starving — customers don't get their notifications because the system is busy generating PDFs for internal reports.

This is the **Noisy Neighbor** problem — one resource-intensive workload degrades service for everyone else.

## ChokaQ's Solution: Database-Level Bulkheads

ChokaQ solves this at the **SQL query level** — inside the `FetchNextBatch` CTE:

```sql
-- From: Queries.cs — FetchNextBatch

WITH ActiveCounts AS (
    -- Count committed active jobs per queue. This is part of capacity control,
    -- so it must not use dirty reads.
    SELECT [Queue], COUNT(1) AS CurrentActive
    FROM [chokaq].[JobsHot]
    WHERE [Status] IN (1, 2)    -- Fetched + Processing
    GROUP BY [Queue]
)
SELECT TOP (@Limit) h.*
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
LEFT JOIN [chokaq].[Queues] q ON h.[Queue] = q.[Name]
LEFT JOIN ActiveCounts ac ON h.[Queue] = ac.[Queue]
WHERE h.[Status] = 0
  AND (q.[IsPaused] IS NULL OR q.[IsPaused] = 0)
  AND (q.[IsActive] IS NULL OR q.[IsActive] = 1)
  -- THE BULKHEAD: simple form shown for readability.
  -- Production SQL ranks candidates per queue and applies:
  -- CurrentActive + QueueRank <= MaxWorkers
  AND (q.[MaxWorkers] IS NULL OR ISNULL(ac.CurrentActive, 0) < q.[MaxWorkers])
  AND (h.[ScheduledAtUtc] IS NULL OR h.[ScheduledAtUtc] <= SYSUTCDATETIME())
ORDER BY h.[Priority] DESC,
         ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

### How It Works

The `Queues` table has a `MaxWorkers` column:

```sql
CREATE TABLE [chokaq].[Queues](
    [Name]                 VARCHAR(255) NOT NULL,
    [IsPaused]             BIT NOT NULL DEFAULT 0,
    [IsActive]             BIT NOT NULL DEFAULT 1,
    [ZombieTimeoutSeconds] INT NULL,
    [MaxWorkers]           INT NULL,    -- 👈 Bulkhead limit
    [LastUpdatedUtc]       DATETIME2(7) NOT NULL
);
```

**Configuration example:**

| Queue | MaxWorkers | Effect |
|-------|-----------|--------|
| `pdf-generation` | `3` | Maximum 3 PDFs processing simultaneously |
| `sms-notifications` | `NULL` | No limit — use all available capacity |
| `email-sending` | `10` | Cap at 10 concurrent email sends |

### The Visualization

![Queue bulkhead isolation](/diagrams/40-queue-bulkhead-isolation.png)

Even with 50 PDF jobs flooding the queue, SMS notifications **never wait more than one polling cycle** — the PDF queue is capped at 3 concurrent slots.

## Why Database-Level?

Most frameworks implement bulkhead at the **application level** — separate thread pools or semaphores per queue. This has a critical flaw:

| Approach | Problem |
|----------|---------|
| **App-level** (thread pools) | If you run multiple instances, each has its own pool. 3 instances × 3 threads = 9 concurrent PDFs, not 3 |
| **Database-level** (ChokaQ) | The `COUNT(*)` check runs inside the fetch query. All instances see the same counts. Limit is global |

::: tip 💡 Architecture Insight
Enforcing the Bulkhead pattern at the database level solves the multi-instance problem. Application-level semaphores do not coordinate between instances. Every worker reads the same `JobsHot` table, so the `ActiveCounts` CTE gives a committed shared view of currently active work. Production SQL also ranks candidates per queue, which prevents one fetch batch from claiming more rows than the queue's remaining capacity.
:::

::: tip 💡 The Scale Trade-off (Why Database-Level?)
You might ask: "Isn't running `COUNT(1)` on the database for every fetch expensive?" 
In a traditional single-table queue design (where history and active jobs are mixed), yes. But because ChokaQ uses the **Three Pillars** architecture, the `JobsHot` table only contains currently active jobs. Executing an indexed `COUNT` on a table with a few hundred or thousand rows is instantaneous for a relational database. 

By trading a microscopic amount of database CPU, you get **perfect, cluster-wide distributed coordination** without the operational burden of deploying and monitoring a separate distributed caching layer.
:::

## Runtime Adjustment

Limits can be changed **at runtime** via The Deck dashboard or API — no restart required:

```csharp
// Via IJobStorage
await _storage.SetQueueMaxWorkersAsync("pdf-generation", maxWorkers: 5);

// Or set to NULL to remove the limit
await _storage.SetQueueMaxWorkersAsync("pdf-generation", maxWorkers: null);
```

The change takes effect on the **next fetch cycle** (within `PollingInterval` seconds).

## Queue Controls Beyond Bulkhead

The `Queues` table provides additional controls:

| Control | Column | Effect |
|---------|--------|--------|
| **Pause** | `IsPaused = 1` | Queue's jobs are skipped during fetch. Already-processing jobs continue. |
| **Deactivate** | `IsActive = 0` | Queue is completely disabled — jobs are released back to Pending |
| **Zombie Timeout** | `ZombieTimeoutSeconds` | Per-queue heartbeat threshold (overrides global setting) |
| **Bulkhead** | `MaxWorkers` | Maximum concurrent processing slots for this queue |

All four controls are exposed in The Deck dashboard and take effect without any restart or redeployment.

## Architecture Decision

ChokaQ enforces queue bulkheads in the storage claim path instead of only inside
one worker process. That matters because production deployments often run
multiple application instances. A local semaphore can cap each process, but it
cannot cap the whole fleet unless every process coordinates through another
shared system.

The SQL fetch query already owns the decision to claim work, so it is the right
place to apply global queue capacity. `MaxWorkers` becomes a shared production
contract: if a queue is capped at three active jobs, three means three across
all hosts that use the same database.

The trade-off is extra SQL work during fetch. ChokaQ accepts that cost because
the Hot table is intentionally small and indexed for active work. The result is
stronger isolation with fewer moving parts.

## Interview Questions

**Why is a database-level bulkhead stronger than per-process worker pools?**  
Because the database sees all active rows from all instances. Per-process pools
multiply capacity by instance count and drift whenever autoscaling changes the
number of hosts.

**Can a bulkhead starve a queue?**  
A queue can be intentionally capped, but it should not starve unrelated queues.
If a queue is capped too low, its own lag grows and The Deck makes that visible
so operators can raise `MaxWorkers` or split the workload.

**What is the risk of counting active jobs in the fetch query?**  
The risk is database overhead. The mitigation is the Three Pillars model:
`JobsHot` only stores active lifecycle rows, so active-count queries stay bounded
by current work instead of archive history.

<br>

> *Next: See how the [ZombieRescueService](/2-lifecycle/zombie-rescue) recovers crashed jobs automatically.*
