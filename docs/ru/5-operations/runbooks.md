# Operations Runbooks

Platform probes и readiness checks описаны в [Health Checks](/ru/5-operations/health-checks). Schema startup behavior - в [Schema Bootstrap And Migrations](/ru/5-operations/schema-bootstrap-and-migrations). History growth и deletion policy - в [Retention Cleanup](/ru/5-operations/retention-cleanup).

Эта страница превращает ChokaQ signals в конкретные operator actions. Язык намеренно прямой: когда что-то красное, главный вопрос - "что проверить первым и чего нельзя делать?"

Используйте эти runbooks вместе с:

- The Deck на `/chokaq`;
- `/health`;
- OpenTelemetry metrics из meter `ChokaQ`;
- application logs с ChokaQ `EventId` values;
- SQL Server monitoring.

## First five minutes

Когда сработал ChokaQ alert, начните здесь.

1. Откройте `/health`.
2. Откройте The Deck и найдите affected queue.
3. Определите, проблема в основном в lag, DLQ, worker liveness или SQL.
4. Проверьте top error families перед retry failed work.
5. Не purge'ите, пока не понимаете, что удаляете.

Самое быстрое неправильное действие часто хуже, чем минута на классификацию incident.

## Runbook: queue lag is high

Queue lag означает, что eligible jobs слишком долго ждут перед start execution. Обычно это первый signal, который чувствуют users, потому что он измеряет delay вместо raw queue depth.

### Symptoms

- `chokaq_queue_saturation` degraded или unhealthy.
- The Deck показывает высокий average/max lag для одной queue.
- `chokaq.jobs.queue_lag` p95/max rising.
- Jobs остаются pending, хотя workers running.

### First checks

| Check | Why |
|---|---|
| Queue paused? | Paused queues intentionally stop draining. |
| Queue inactive? | Inactive queues may be hidden from normal operations. |
| `MaxWorkers` too low? | Queue-level isolation can cap throughput. |
| Workers active? | No active workers means host is not processing. |
| SQL healthy? | SQL bottlenecks slow fetch/finalization and can make scaling worse. |
| Handlers slow? | Long processing duration can consume all worker capacity. |
| Circuits open? | Circuit may block repeated failing work. |
| Lag isolated to one queue? | One slow workload should not drive global changes. |

### Safe actions

- Если SQL и downstream dependencies healthy, добавьте workers или поднимите `MaxWorkers` affected queue.
- Если одна heavy queue starving others, split it into separate queue или lower its `MaxWorkers`.
- Если queue была paused случайно, resume и смотрите, как lag drains.
- Если producers отправляют work быстрее consumers, reduce producer rate или add capacity.
- Если handlers slow but expected, рассмотрите queue-specific `Queues:{name}:ExecutionTimeout` и capacity plan.

### Avoid

- Не добавляйте workers, когда SQL already unhealthy; это может amplify storage pressure.
- Не поднимайте concurrency для downstream-limited queue, которая уже returning `Throttled`.
- Не purge'ите pending work ради красивых charts, если business явно не решил, что work obsolete.

### Confirm recovery

Recovery видно, когда:

- max queue lag falls;
- pending count drains;
- throughput rises without matching DLQ spike;
- `/health` returns healthy;
- SQL waits и command timeouts stay normal.

## Runbook: DLQ spike

DLQ rows - jobs, которые вышли из normal execution path. Они не одинаковые. Failure reason определяет next action.

### Symptoms

- `chokaq.jobs.dlq` rises.
- The Deck Top Errors widget показывает repeated error family.
- Failure rate в The Deck или metrics increases.
- `/health` может быть healthy; DLQ - correctness signal, не всегда platform-liveness failure.

### Classify by failure reason

| Failure reason | Meaning | Typical first action | Requeue safety |
|---|---|---|---|
| `Fatal` | Handler или payload failed non-transient. | Inspect payload, handler code, type key и recent deployments. | Unsafe until code/data repaired. |
| `Throttled` | Downstream dependency requested slower traffic. | Reduce concurrency или wait for quota recovery. | Often unsafe immediately; requeue later or at lower concurrency. |
| `Timeout` | Handler exceeded execution timeout. | Check dependency latency и handler duration. | Safe only if side effects idempotent or timeout before side effects. |
| `MaxRetriesExceeded` | Retryable failures exhausted attempts. | Inspect repeated error family и downstream health. | Depends on cause; targeted requeue. |
| `Cancelled` | Operator или shutdown path cancelled work. | Confirm actor/reason before retry. | Usually intentional; requeue only if cancellation mistaken. |
| `Zombie` | Processing heartbeat expired. | Inspect side effects before requeue. | Potentially unsafe; user code may already have run. |

### Safe actions

- Filter DLQ by failure reason and job type.
- Inspect sample payloads and error details.
- Use preview before filtered requeue or purge.
- Repair payloads before requeue when failure data-driven.
- Requeue small targeted subset first and watch results.

### Avoid

- Не bulk requeue all DLQ rows только потому, что downstream service вернулся.
- Не purge before confirming retention, audit и business impact.
- Не считайте zombie work unstarted work: user code мог выполнить external side effects before heartbeat expiry.

### Confirm recovery

Recovery видно, когда same error family stops growing, requeued rows succeed или fail for new understood reason, failure rate normalizes, а queue lag не spikes from recovery traffic.

## Runbook: worker health is unhealthy

Worker health означает, что hosted ChokaQ worker loop alive и recently heartbeat. Web process может отвечать HTTP, пока background worker stopped или stuck.

### Symptoms

- `chokaq_worker` unhealthy.
- The Deck показывает stale worker data.
- Queue lag rises while enqueue rate continues.
- Application logs show worker loop start/stop/crash events.

### First checks

| Check | Why |
|---|---|
| Did the process restart? | Workers stop during deployment or crash. |
| SQL reachable? | SQL worker cannot fetch/finalize without storage. |
| Host overloaded? | CPU/thread-pool pressure can delay worker loops. |
| All queues paused? | Worker may sleep when no active queue. |
| Fetcher loop crashes in logs? | Fetch errors can pause progress while host up. |
| Shutdown in progress? | Worker shutdown can wait for active jobs to drain. |

### Safe actions

- Restart host, если worker loop stuck и SQL healthy.
- Check logs for `SqlInitializationFailed`, fetcher crashes и processor loop errors.
- Confirm abandoned `Fetched` jobs recover after configured timeout.
- Если worker repeatedly fails on startup, stop app и fix storage/configuration instead of repeated restarts.

### Avoid

- Не редактируйте SQL rows вручную без понимания ownership и state transitions.
- Не shorten zombie timeouts aggressively to force progress; это может move still-running side-effecting work to DLQ.

## Runbook: SQL health is unhealthy

SQL Server - durable coordination boundary в SQL mode. Если он unhealthy, workers могут быть alive, но не able to admit, fetch, heartbeat, finalize или show dashboard state reliably.

### Symptoms

- `chokaq_sql` unhealthy.
- SQL command timeouts in logs.
- Heartbeat failures rise.
- Queue lag rises across multiple queues.
- Fetcher loop logs repeated storage errors.

### First checks

- SQL Server reachable from app host?
- Credentials, DNS, port, firewall или certificate settings changed?
- Database online?
- Schema initialization failed?
- Blocking sessions или long-running maintenance deletes?
- Transaction log full or slow?
- CPU, memory, disk и tempdb under pressure?

### Safe actions

- Restore SQL connectivity first.
- Reduce worker count или queue `MaxWorkers`, если SQL overloaded.
- Pause non-critical queues, если critical queues должны recover first.
- Delay maintenance cleanup until active work healthy.
- Keep `CancelOnHeartbeatFailure = false`, если fail-fast behavior не required intentionally.

### Avoid

- Не добавляйте worker hosts, пока SQL bottleneck.
- Не запускайте large manual deletes during active queue-lag incident.
- Не считайте heartbeat failures признаком broken handler code.

## Runbook: throttled downstream dependency

Throttling значит, что другая система просит slow down. ChokaQ может classify и retry throttled failures, но не может увеличить external quota.

### Symptoms

- DLQ reason или logs show `Throttled`.
- Retry-after delays appear.
- Circuit events rise for one job type.
- Queue lag rises for downstream-specific queue.

### Safe actions

- Lower queue `MaxWorkers` для affected dependency.
- Pause affected queue, если dependency попросила long cooldown.
- Requeue only after quota recovery.
- Split dependency into its own queue, если она shares capacity with unrelated work.
- Make handlers use provider-side idempotency keys where available.

### Avoid

- Не scale workers up. More workers can make throttling worse.
- Не bulk requeue throttled DLQ rows, пока dependency still rejecting traffic.

## Runbook: payload or contract defect

Background jobs - persisted messages. Когда type key и payload shape уже в SQL, deployed code должен still know how to read them.

### Symptoms

- Fatal DLQ rows after deployment.
- Type-key troubleshooting points to missing registration.
- Deserialization или validation errors repeat.
- Only one job type affected.

### Safe actions

- Confirm persisted type key registered in `ChokaQJobProfile`.
- Compare old and new payload shape.
- If data repairable, use The Deck edit-and-requeue workflow.
- If code compatibility broke, deploy compatible handler or migration path.
- Requeue small repaired subset before bulk recovery.

### Avoid

- Не rename type keys casually. Type keys - persisted message-contract identifiers.
- Не purge old failures until you know whether they contain business work that still matters.

## Runbook: bulk recovery

Bulk controls - recovery tools. Они также могут amplify mistake.

### Use bulk requeue when

- root cause fixed;
- target rows filtered by type, reason, time, tag или error family;
- payloads do not need individual repair;
- side effects idempotent или failure happened before side effects;
- preview count and sample IDs match intended blast radius.

### Use edit-and-requeue when

- payloads have bad values;
- schema/contract changes require small data correction;
- only few rows need repair;
- you need one atomic operator action that fixes and requeues.

### Use cancel when

- active work is no longer wanted;
- queue contains obsolete jobs;
- downstream systems should not receive the work anymore.

### Use purge when

- retention policy allows deletion;
- rows have no remaining business value;
- audit requirements satisfied;
- preview count and filters correct.

### Avoid

- Не purge as first incident response.
- Не requeue all DLQ rows from multiple failure classes at once.
- Не run recovery traffic at full concurrency against just-recovered dependency.

## Runbook: state transition conflicts

State transition conflicts происходят, когда worker пытается mutate row, но row missing, wrong state или больше не owned by that worker. Обычно это safety mechanism doing its job.

### Symptoms

- `chokaq.jobs.state_transition_conflicts` rises.
- Logs show state transitions not applied.
- Worker woke up after timeout, restart или zombie recovery.

### First checks

- Worker shutdown или restart happened?
- Execution timeouts too short for handler?
- Heartbeat intervals и zombie timeouts too aggressive?
- SQL slow enough that leases expire while work active?
- Handler ignoring cancellation?

### Safe actions

- Treat isolated conflicts as expected protection against stale workers.
- Investigate repeated conflicts by queue and job type.
- Tune execution timeout и heartbeat settings, если legitimate long work reclaimed too early.
- Make handlers observe cancellation tokens promptly.

### Avoid

- Не remove ownership guards to make conflicts disappear. Guard prevents stale finalization.

## Incident notes template

Используйте этот minimal template для serious incidents:

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

Это не заменяет full incident process. Это держит ChokaQ-specific facts в одном месте, чтобы team могла learn from event.
