# The Deck Panel Guide

![The Deck dashboard](/screenshots/the-deck/dashboard.png)

The Deck is the operational surface for ChokaQ. It exists for the moment when a
queue is not just a data structure, but a live production system with lag,
failures, retries, stalled work, and operator decisions.

For security boundaries, see [Authorization Model](/4-the-deck/authorization-model).
For mutation safety, see [Destructive Actions](/4-the-deck/destructive-actions).
For live updates, see [SignalR Notification Contract](/4-the-deck/signalr-notification-contract).

## Dashboard Areas

| Area | Purpose |
|---|---|
| Stats | Global throughput, success/failure counts, and recent activity. |
| Health | Queue lag, worker liveness, SQL connectivity, and saturation signals. |
| Queues | Per-queue pause state, max worker controls, zombie timeout controls. |
| Circuits | Circuit breaker state and failure protection visibility. |
| Job Matrix | Active, archive, and DLQ job inspection. |
| Ops Panel | Payload inspection, edit, resurrection, and bulk operations. |
| Console Log | Operator-visible runtime events and admin action feedback. |

## Queue Controls

The queue panel is not a decoration. It changes runtime behavior.

| Control | Effect | Risk |
|---|---|---|
| Pause queue | Workers stop fetching new work for that queue. | Existing processing work may continue. |
| Max workers | Caps concurrent fetched/processing work per queue. | Too low creates lag; too high can overload downstream. |
| Zombie timeout | Overrides global processing heartbeat timeout. | Too low can DLQ healthy long-running jobs. |

Operators should treat queue controls as production throttles.

## Job Matrix

![The Deck queue list](/screenshots/the-deck/queue-list.png)

The Job Matrix is the main inspection surface.

For history query behavior, see [Paging, Sorting And Filtering](/4-the-deck/paging-sorting-filtering).

| Source | What it shows | Typical action |
|---|---|---|
| Active | Pending, fetched, processing, delayed retry. | Confirm progress or cancel safe jobs. |
| Archive | Succeeded jobs. | Audit and confirm completion. |
| DLQ | Failed/cancelled/zombie jobs. | Inspect, edit, resurrect, or purge. |

## Job Details

![The Deck job details](/screenshots/the-deck/job-details.png)

Job details should answer:

- what job type is this;
- which queue owns it;
- what payload was stored;
- how many attempts ran;
- when it was created/scheduled/started/finished;
- which worker owned it;
- why it failed;
- whether resurrection is safe.

## DLQ Actions

![The Deck DLQ](/screenshots/the-deck/dlq.png)

| Action | Meaning | Safety requirement |
|---|---|---|
| Inspect | Read failure reason and payload. | Safe. |
| Edit | Change payload/tags before requeue. | Requires understanding handler contract. |
| Resurrect | Move DLQ row back to Hot. | Safe only if side effects are idempotent. |
| Purge | Permanently delete DLQ row. | Destructive; should require confirmation and policy. |
| Bulk requeue | Requeue many DLQ rows by filter. | Requires tight filters and preview. |
| Bulk purge | Delete many DLQ rows by filter. | Highest-risk operator action. |

## Operator Workflow

1. Check health and queue lag.
2. Identify the queue and type key causing failure.
3. Open the DLQ row.
4. Read `FailureReason` before reading the stack trace.
5. Decide whether the failure is transient, permanent, stale, duplicate, or a
   zombie side-effect risk.
6. Fix the cause before resurrection.
7. Resurrect a small sample before bulk actions.
8. Watch throughput and failure rate after requeue.

## Architecture Decision

### Why this pattern?

Durable background work needs an operator surface because failure is not purely
a developer concern. Once jobs are in SQL, the system needs safe ways to inspect
and repair them without direct table edits.

### Trade-offs

A dashboard introduces authorization, destructive-action safety, and UI
correctness responsibilities. The payoff is that production recovery no longer
requires ad hoc SQL surgery.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| SQL-only operations | No dashboard to maintain. | Error-prone and inaccessible for most operators. |
| Logs-only diagnosis | Easy to add. | Logs do not expose repair actions or current state. |
| External APM only | Good metrics. | Does not own job lifecycle decisions. |

### Additional Questions

**Why does a queue engine need a UI?**  
Because DLQ, pause, bulk requeue, and circuit inspection are operational
workflows, not just metrics.

**How do you prevent accidental destructive actions?**  
Use authorization policies, typed confirmations, previews, and bounded filters.

**What if SignalR is stale?**  
SignalR is a notification layer. Storage remains authoritative; operators
should refresh/read committed state when in doubt.
