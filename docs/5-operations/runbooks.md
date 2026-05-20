# Operations Runbooks

For platform probes and readiness checks, see [Health Checks](/5-operations/health-checks).
For schema startup behavior, see [Schema Bootstrap And Migrations](/5-operations/schema-bootstrap-and-migrations).
For history growth and deletion policy, see [Retention Cleanup](/5-operations/retention-cleanup).

This page turns ChokaQ signals into concrete operator actions. It intentionally
uses plain language: when something is red, the important question is "what do I
check first and what should I avoid doing?"

Use these runbooks with:

- The Deck at `/chokaq`;
- `/health`;
- OpenTelemetry metrics from the `ChokaQ` meter;
- application logs with ChokaQ `EventId` values;
- SQL Server monitoring.

## First Five Minutes

When a ChokaQ alert fires, start here.

1. Open `/health`.
2. Open The Deck and identify the affected queue.
3. Check whether the problem is mostly lag, DLQ, worker liveness, or SQL.
4. Check top error families before retrying failed work.
5. Avoid purge until you know what you are deleting.

The fastest wrong action is often worse than waiting one minute to classify the
incident.

## Runbook: Queue Lag Is High

Queue lag means eligible jobs are waiting too long before execution starts. This
is usually the first signal users feel, because it measures delay instead of raw
queue depth.

### Symptoms

- `chokaq_queue_saturation` is degraded or unhealthy.
- The Deck shows high average or max lag for one queue.
- `chokaq.jobs.queue_lag` p95/max is rising.
- Jobs are pending even though workers are running.

### First Checks

| Check | Why |
|---|---|
| Is the queue paused? | Paused queues intentionally stop draining. |
| Is the queue inactive? | Inactive queues may be hidden from normal operations. |
| Is `MaxWorkers` too low? | Queue-level isolation can cap throughput. |
| Are workers active? | No active workers means the host is not processing. |
| Is SQL healthy? | SQL bottlenecks slow fetch/finalization and can make scaling worse. |
| Are handlers slow? | Long processing duration can consume all worker capacity. |
| Are circuits open? | A circuit may be blocking repeated failing work. |
| Is the lag isolated to one queue? | One slow workload should not drive global changes. |

### Safe Actions

- If SQL and downstream dependencies are healthy, add workers or raise the
  affected queue's `MaxWorkers`.
- If one heavy queue is starving others, split it into a separate queue or lower
  its `MaxWorkers`.
- If the queue was paused accidentally, resume it and watch lag drain.
- If producers are sending work faster than consumers can process, reduce
  producer rate or add capacity.
- If handlers are slow but expected, consider a queue-specific
  `Queues:{name}:ExecutionTimeout` and capacity plan.

### Avoid

- Do not add workers when SQL is already unhealthy; more workers can amplify
  storage pressure.
- Do not raise concurrency for a downstream-limited queue that is already
  returning `Throttled`.
- Do not purge pending work to make charts look better unless the business has
  explicitly decided the work is obsolete.

### Confirm Recovery

Recovery is visible when:

- max queue lag falls;
- pending count drains;
- throughput rises without a matching DLQ spike;
- `/health` returns healthy;
- SQL waits and command timeouts stay normal.

## Runbook: DLQ Spike

DLQ rows are jobs that left the normal execution path. They are not all the
same. The failure reason decides the next action.

### Symptoms

- `chokaq.jobs.dlq` rises.
- The Deck Top Errors widget shows a repeated error family.
- Failure rate in The Deck or metrics increases.
- `/health` may still be healthy; DLQ is a correctness signal, not always a
  platform-liveness failure.

### Classify By Failure Reason

| Failure reason | Meaning | Typical first action | Requeue safety |
|---|---|---|---|
| `Fatal` | Handler or payload failed in a non-transient way. | Inspect payload, handler code, type key, and recent deployments. | Unsafe until code/data is repaired. |
| `Throttled` | Downstream dependency requested slower traffic. | Reduce concurrency or wait for quota recovery. | Often unsafe immediately; requeue later or at lower concurrency. |
| `Timeout` | Handler exceeded execution timeout. | Check dependency latency and handler duration. | Safe only if side effects are idempotent or timeout happened before side effects. |
| `MaxRetriesExceeded` | Retryable failures exhausted attempts. | Inspect repeated error family and downstream health. | Depends on cause; do targeted requeue. |
| `Cancelled` | Operator or shutdown path cancelled work. | Confirm actor/reason before retry. | Usually intentional; requeue only if cancellation was mistaken. |
| `Zombie` | Processing heartbeat expired. | Inspect side effects before requeue. | Potentially unsafe; user code may already have run. |

### Safe Actions

- Filter DLQ by failure reason and job type.
- Inspect a sample of payloads and error details.
- Use preview before filtered requeue or purge.
- Repair payloads before requeue when the failure is data-driven.
- Requeue a small targeted subset first and watch results.

### Avoid

- Do not bulk requeue all DLQ rows just because the downstream service is back.
- Do not purge before confirming retention, audit, and business impact.
- Do not treat zombie work as unstarted work; user code may have performed
  external side effects before heartbeat expiry.

### Confirm Recovery

Recovery is visible when:

- the same error family stops growing;
- requeued rows succeed or fail for a new understood reason;
- failure rate returns to normal;
- queue lag does not spike from recovery traffic.

## Runbook: Worker Health Is Unhealthy

Worker health means the hosted ChokaQ worker loop is alive and recently
heartbeat. A web process can still answer HTTP while its background worker is
stopped or stuck.

### Symptoms

- `chokaq_worker` is unhealthy.
- The Deck shows stale worker data.
- Queue lag rises while enqueue rate continues.
- Application logs show worker loop start/stop/crash events.

### First Checks

| Check | Why |
|---|---|
| Did the process restart? | Workers stop during deployment or crash. |
| Is SQL reachable? | SQL worker cannot fetch or finalize work without storage. |
| Is the host overloaded? | CPU/thread-pool pressure can delay worker loops. |
| Are all queues paused? | The worker may sleep when there is no active queue. |
| Are logs showing fetcher loop crashes? | Fetch errors can pause progress even while the host is up. |
| Is shutdown in progress? | Worker shutdown can wait for active jobs to drain. |

### Safe Actions

- Restart the host if the worker loop is stuck and SQL is healthy.
- Check logs for `SqlInitializationFailed`, fetcher crashes, and processor loop
  errors.
- Confirm abandoned `Fetched` jobs recover after the configured timeout.
- If the worker repeatedly fails on startup, stop the app and fix storage or
  configuration instead of repeated restarts.

### Avoid

- Do not manually edit SQL rows unless you understand ownership and state
  transitions.
- Do not shorten zombie timeouts aggressively to force progress; that can move
  still-running side-effecting work to DLQ.

## Runbook: SQL Health Is Unhealthy

SQL Server is the durable coordination boundary in SQL mode. If it is unhealthy,
workers may be alive but unable to admit, fetch, heartbeat, finalize, or show
dashboard state reliably.

### Symptoms

- `chokaq_sql` is unhealthy.
- SQL command timeouts appear in logs.
- Heartbeat failures rise.
- Queue lag rises across multiple queues.
- Fetcher loop logs repeated storage errors.

### First Checks

- Is the SQL Server reachable from the app host?
- Did credentials, DNS, port, firewall, or certificate settings change?
- Is the database online?
- Did schema initialization fail?
- Are there blocking sessions or long-running maintenance deletes?
- Is the transaction log full or slow?
- Are CPU, memory, disk, and tempdb under pressure?

### Safe Actions

- Restore SQL connectivity first.
- Reduce worker count or queue `MaxWorkers` if SQL is overloaded.
- Pause non-critical queues if critical queues must recover first.
- Delay maintenance cleanup until active work is healthy.
- Keep `CancelOnHeartbeatFailure = false` unless fail-fast behavior is
  intentionally required for this environment.

### Avoid

- Do not add more worker hosts while SQL is the bottleneck.
- Do not run large manual deletes during an active queue-lag incident.
- Do not assume heartbeat failures mean handler code is broken.

## Runbook: Throttled Downstream Dependency

Throttling means another system is telling you to slow down. ChokaQ can classify
and retry throttled failures, but it cannot increase an external quota.

### Symptoms

- DLQ reason or failure logs show `Throttled`.
- Retry-after delays appear.
- Circuit events rise for one job type.
- Queue lag rises for a downstream-specific queue.

### Safe Actions

- Lower queue `MaxWorkers` for the affected dependency.
- Pause the affected queue if the dependency asked for a longer cooldown.
- Requeue only after quota recovery.
- Consider splitting the dependency into its own queue if it currently shares
  capacity with unrelated work.
- Make handlers use provider-side idempotency keys where available.

### Avoid

- Do not scale workers up. More workers can make throttling worse.
- Do not bulk requeue throttled DLQ rows while the dependency is still rejecting
  traffic.

## Runbook: Payload Or Contract Defect

Background jobs are persisted messages. Once a type key and payload shape are in
SQL, deployed code must still know how to read them.

### Symptoms

- Fatal DLQ rows after deployment.
- Type-key troubleshooting points to missing registration.
- Deserialization or validation errors repeat.
- Only one job type is affected.

### Safe Actions

- Confirm the persisted type key is registered in a `ChokaQJobProfile`.
- Compare old and new payload shape.
- If data is repairable, use The Deck edit-and-requeue workflow.
- If code compatibility broke, deploy a compatible handler or migration path.
- Requeue a small repaired subset before bulk recovery.

### Avoid

- Do not rename type keys casually. Type keys are persisted message-contract
  identifiers.
- Do not purge old failures until you know whether they contain business work
  that still matters.

## Runbook: Bulk Recovery

Bulk controls are recovery tools. They can also amplify a mistake.

### Use Bulk Requeue When

- the root cause is fixed;
- the target rows are filtered by type, reason, time, tag, or error family;
- payloads do not need individual repair;
- side effects are idempotent or the failure happened before side effects;
- preview count and sample IDs match your intended blast radius.

### Use Edit-And-Requeue When

- payloads have bad values;
- schema or contract changes require a small data correction;
- only a few rows need repair;
- you need one atomic operator action that fixes and requeues.

### Use Cancel When

- active work is no longer wanted;
- a queue contains obsolete jobs;
- downstream systems should not receive the work anymore.

### Use Purge When

- retention policy allows deletion;
- the rows have no remaining business value;
- audit requirements are satisfied;
- preview count and filters are correct.

### Avoid

- Do not purge as the first response to an incident.
- Do not requeue all DLQ rows from multiple failure classes at once.
- Do not run recovery traffic at full concurrency against a just-recovered
  dependency.

## Runbook: State Transition Conflicts

State transition conflicts happen when a worker tries to mutate a row but the
row is missing, in the wrong state, or no longer owned by that worker. This is
usually a safety mechanism doing its job.

### Symptoms

- `chokaq.jobs.state_transition_conflicts` rises.
- Logs show state transitions not applied.
- A worker woke up after timeout, restart, or zombie recovery.

### First Checks

- Did worker shutdown or restart happen?
- Are execution timeouts too short for the handler?
- Are heartbeat intervals and zombie timeouts too aggressive?
- Is SQL slow enough that leases expire while work is still active?
- Is a handler ignoring cancellation?

### Safe Actions

- Treat isolated conflicts as expected protection against stale workers.
- Investigate repeated conflicts by queue and job type.
- Tune execution timeout and heartbeat settings if legitimate long work is
  being reclaimed too early.
- Make handlers observe cancellation tokens promptly.

### Avoid

- Do not remove ownership guards to make conflicts disappear. The guard is what
  prevents stale finalization.

## Incident Notes Template

Use this minimal template for serious incidents:

| Field | Value |
|---|---|
| Start time |  |
| End time |  |
| Affected queues |  |
| Affected job types |  |
| Primary signal |  |
| Top failure reason |  |
| Top error family |  |
| First action taken |  |
| Recovery action |  |
| Rows requeued/cancelled/purged |  |
| Follow-up code/docs/config change |  |

This does not replace a full incident process. It keeps ChokaQ-specific facts in
one place so the team can learn from the event.
