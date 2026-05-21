# Zombie Rescue Service

## What is a Zombie Job?

A zombie is a job that was being processed when the worker **crashed, was killed, or lost connectivity**. The job is stuck in `Processing` status forever — it will never complete, but no one knows it's dead.

```
Timeline:
─────────────────────────────────────────────
  t=0     Worker fetches Job-42, starts processing
  t=5s    Worker updates heartbeat → HeartbeatUtc = t=5s
  t=10s   Worker updates heartbeat → HeartbeatUtc = t=10s
  t=12s   ⚡ WORKER CRASHES (OOM, server restart, k8s pod eviction)
  t=15s   No heartbeat update...
  t=20s   No heartbeat update...
  t=600s  Job-42 has been "Processing" for 10 minutes with no heartbeat
          → It's a ZOMBIE
```

Without intervention, this job would sit in `Processing` status **indefinitely**, blocking the queue slot (in Bulkhead mode) and never completing.

![Zombie rescue sweep](/diagrams/33-zombie-rescue-sweep.png)

## The ZombieRescueService

A `BackgroundService` that runs on `ChokaQOptions.Recovery.ScanInterval` with two recovery phases:

```csharp
// From: ChokaQ.Core/Resilience/ZombieRescueService.cs

protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            // Phase 1: Recover abandoned jobs (Fetched but never processed)
            var recovered = await _storage.RecoverAbandonedAsync(
                options.FetchedJobTimeoutSeconds, ct);

            // Phase 2: Archive true zombies (Processing with expired heartbeat)
            var archived = await _storage.ArchiveZombiesAsync(
                options.ZombieTimeoutSeconds, ct);

            if (recovered > 0 || archived > 0)
            {
                _logger.LogWarning(
                    "ZombieRescue: Recovered {Recovered} abandoned, " +
                    "archived {Archived} zombies", recovered, archived);

                // Notify dashboard
                await _notifier.NotifyZombieRescueAsync(recovered, archived);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZombieRescue sweep failed");
        }

        await Task.Delay(options.Recovery.ScanInterval, ct);
    }
}
```

## Phase 1: Recover Abandoned Jobs

**Problem:** Worker fetched a batch of jobs (`Status = Fetched`) but crashed before marking them as `Processing`. These jobs are locked but never started.

**Solution:** Reset them to `Pending`:

```sql
-- RecoverAbandonedAsync
UPDATE [chokaq].[JobsHot]
SET [Status] = 0,           -- Back to Pending
    [WorkerId] = NULL,       -- Clear worker assignment
    [LastUpdatedUtc] = SYSUTCDATETIME()
WHERE [Status] = 1           -- Fetched
  AND DATEDIFF(SECOND, [LastUpdatedUtc], SYSUTCDATETIME()) > @TimeoutSeconds
```

These jobs return to the queue because user code has not started yet. The next
fetch cycle can pick them up.

`Recovery.FetchedJobTimeout` is intentionally separate from `Recovery.ProcessingZombieTimeout`.
Fetched jobs are only reserved in a worker's prefetch buffer; user code has not
run yet, so recovery is a safe retry. Processing jobs are different: they may
already have side effects, so their timeout is heartbeat-based and leads to DLQ.
Keeping the two timeouts independent prevents a short processing heartbeat policy
from reclaiming healthy jobs that are merely waiting for an execution slot.

The old `FetchedJobTimeoutSeconds` and `ZombieTimeoutSeconds` properties still
exist as compatibility aliases. New hosts should prefer the nested `Recovery`
section because it maps cleanly to `appsettings.json`.

## Phase 2: Archive True Zombies

**Problem:** Worker started processing (`Status = Processing`) but stopped updating the heartbeat. The job is genuinely stuck.

**Solution:** Move to DLQ with `FailureReason.Zombie`:

```sql
-- ArchiveZombiesAsync — with per-queue timeout support
DELETE FROM [chokaq].[JobsHot]
OUTPUT
    DELETED.[Id], DELETED.[Queue], DELETED.[Type], DELETED.[Payload],
    DELETED.[Tags],
    2,                        -- FailureReason.Zombie
    'Zombie detected: heartbeat expired',
    DELETED.[AttemptCount], DELETED.[WorkerId],
    DELETED.[CreatedBy], NULL,
    DELETED.[CreatedAtUtc], SYSUTCDATETIME()
INTO [chokaq].[JobsDLQ](...)
FROM [chokaq].[JobsHot] h
LEFT JOIN [chokaq].[Queues] q ON h.[Queue] = q.[Name]
WHERE h.[Status] = 2          -- Only Processing jobs
  AND DATEDIFF(SECOND,
      ISNULL(h.[HeartbeatUtc], h.[LastUpdatedUtc]),
      SYSUTCDATETIME()
  ) > ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout)
```

::: warning 🎯 Per-Queue Timeouts
Notice the `ISNULL(q.[ZombieTimeoutSeconds], @GlobalTimeout)`. A quick API call (queue `sms`) might have a 30-second timeout, while a heavy PDF generation (queue `reports`) might have a 30-minute timeout. The ZombieRescueService respects these individual thresholds.
:::

## The Heartbeat Contract

During processing, the `JobProcessor` runs a parallel heartbeat task:

```
Job processing:  ████████████████████████████████ (60 seconds)
Heartbeat:       ↑    ↑    ↑    ↑    ↑    ↑    ↑ (every ~10 seconds)
                 │    │    │    │    │    │    │
                 └────┴────┴────┴────┴────┴────┘
                   HeartbeatUtc updated each tick
```

**The formula:**
- `Execution.HeartbeatIntervalMin/Max` = jittered heartbeat window used by the worker (~8-12s by default)
- `Execution.HeartbeatFailureThreshold` = consecutive heartbeat write failures before degraded heartbeat state is logged and counted (~10 by default)
- `Recovery.FetchedJobTimeout` = how long a Fetched-but-not-started reservation may sit before it is returned to Pending
- `Recovery.ProcessingZombieTimeout` = how long a Processing job may miss heartbeats before it is declared dead (~600s)
- `Recovery.ProcessingZombieTimeout` must be **significantly larger** than the heartbeat interval

Heartbeat write failures are reported through `chokaq.jobs.heartbeat_failures`
separately from handler failures. By default they do not cancel user code; set
`Execution.CancelOnHeartbeatFailure` only when the deployment wants heartbeat
storage pressure to fail running work fast.

If `HeartbeatUtc` hasn't been updated in `Recovery.ProcessingZombieTimeout`, the job is either:
- Genuinely stuck (infinite loop, deadlock)
- The worker process died

Either way, the ZombieRescueService safely archives it.

## Recovery vs Archive: The Decision

| Condition | Action | Why |
|-----------|--------|-----|
| `Status = Fetched` + expired | **Recover** → reset to Pending | Worker crashed after fetch but before processing. Job is untouched, so it can return to the queue. |
| `Status = Processing` + expired heartbeat | **Archive** → move to DLQ | Worker crashed during execution. Job may be partially processed, so it needs inspection before retry. |

::: danger Processing zombies require inspection
A zombie in `Processing` state may have **partially completed** side effects:
sent an email, charged a credit card, or updated a database. ChokaQ moves these
jobs to DLQ so an operator can inspect the state and decide whether resurrection
is safe for that handler and payload.
:::

## Dashboard Integration

When the ZombieRescueService detects zombies, it notifies The Deck via SignalR:

1. **Console event:** `"⚠️ ZombieRescue: Recovered 2 abandoned, archived 1 zombie"`
2. **Stats update:** DLQ counter increments in real-time
3. **Job list:** The zombie appears in the DLQ panel with `FailureReason = Zombie`
4. **Admin action:** Inspector shows heartbeat details, admin can Resurrect or Purge

<br>

> *Now let's go deeper. See [Heartbeat](/2-lifecycle/heartbeat) for the liveness contract and [SQL Concurrency (UPDLOCK)](/3-deep-dives/sql-concurrency) for the full breakdown of how 50 workers fetch without deadlocks.*

## Architecture Decision

Zombie rescue separates two failure states that look similar but have different
safety implications. `Fetched` jobs were reserved by a worker but user code did
not start, so they can safely return to Pending. `Processing` jobs may already
have produced side effects, so ChokaQ moves them to DLQ instead of retrying them
automatically.

The alternative is automatic retry for every expired lease. That maximizes
throughput recovery, but it can duplicate side effects after a process crash.
ChokaQ chooses operator-visible safety because background jobs frequently
interact with email, payments, files, and external APIs.

The result is a conservative recovery model: untouched reservations are
reclaimed automatically, while uncertain executions require inspection,
idempotency confidence, and an explicit resurrect decision.

## Additional Questions

**Why are Fetched and Processing jobs handled differently?**  
Fetched means the row is in a local prefetch buffer and user code has not run.
Processing means the handler started and may have performed side effects.

**Why not let heartbeat expiration retry the job immediately?**  
Because heartbeat loss tells you the worker stopped reporting, not whether the
business operation completed. Retrying without inspection can duplicate external
effects.

**How should operators decide whether to resurrect a zombie?**  
They should inspect the handler's idempotency guarantees, downstream state, and
payload. If the operation is idempotent or externally confirmed as incomplete,
resurrection is reasonable; otherwise keep it in DLQ or purge according to
policy.
