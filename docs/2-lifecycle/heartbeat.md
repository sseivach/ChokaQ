# Heartbeat

![Heartbeat contract](/diagrams/32-heartbeat-contract.png)

Heartbeat is how ChokaQ distinguishes a long-running healthy job from a job
owned by a dead worker.

There are two heartbeat concepts:

| Heartbeat | Stored where | Purpose |
|---|---|---|
| Worker heartbeat | Process memory through `IWorkerManager.LastHeartbeatUtc` | Proves the local worker loop is alive for health checks. |
| Job heartbeat | `JobsHot.HeartbeatUtc` | Proves a specific processing job is still owned by a live execution. |

## Job Heartbeat Contract

When a job moves to `Processing`, ChokaQ sets `StartedAtUtc` and `HeartbeatUtc`.
While the handler is running, a heartbeat task periodically updates
`HeartbeatUtc`.

Zombie rescue later checks processing rows whose heartbeat is older than the
configured timeout. Those rows move to DLQ as `Zombie` so an operator can
inspect side-effect risk before resurrection.

## Why Heartbeat Instead Of Start Time?

Start time alone cannot distinguish a healthy long-running report from a dead
worker. Heartbeat gives the worker a way to say: "This job is still executing."

## Configuration

Important settings:

| Setting | Meaning |
|---|---|
| `Execution.HeartbeatIntervalMin` / `HeartbeatIntervalMax` | Jittered interval for per-job heartbeat writes. |
| `Execution.HeartbeatFailureThreshold` | Number of failed writes before degraded heartbeat state is reported. |
| `Execution.CancelOnHeartbeatFailure` | Whether heartbeat write failure should cancel execution. |
| `Recovery.ProcessingZombieTimeout` | Age after which a missing heartbeat turns processing work into zombie DLQ. |
| `Queues.*.ZombieTimeoutSeconds` | Optional per-queue override. |

The zombie timeout must be comfortably larger than the heartbeat interval.

## Failure Path

If heartbeat writes fail, ChokaQ records `chokaq.jobs.heartbeat_failures`.
Depending on policy, the job may continue or be cancelled. If the process dies,
heartbeat stops and zombie rescue eventually moves the job to DLQ.

## Architecture Decision

### Why this pattern?

Heartbeat is a lease freshness signal. It is cheaper and more explicit than
assuming every long-running job is dead after a fixed start-time timeout.

### Trade-offs

Heartbeat writes add database traffic. The interval must be long enough to avoid
write noise and short enough to detect dead workers within the operational
budget.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Timeout from `StartedAtUtc` only | Simple. | Kills healthy long-running jobs. |
| External worker registry only | Good process visibility. | Does not prove individual job progress. |
| No heartbeat | Less database traffic. | Dead workers leave processing rows ambiguous. |

### Interview questions

**Why move heartbeat-expired jobs to DLQ?**  
Because user code may have already produced side effects. The operator must
decide whether retry is safe.

**What timeout values are dangerous?**  
Values close to the heartbeat interval. Normal jitter or transient SQL latency
can make healthy jobs look dead.

**What metric matters here?**  
`chokaq.jobs.heartbeat_failures`, plus DLQ rows with `FailureReason = Zombie`.
