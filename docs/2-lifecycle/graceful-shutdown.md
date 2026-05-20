# Graceful Shutdown

![Graceful shutdown and prefetch release](/diagrams/48-graceful-shutdown-prefetch-release.png)

Graceful shutdown is the path that prevents prefetched and running work from
turning into ambiguous state when the host stops.

ChokaQ separates shutdown behavior for:

- fetcher loop;
- local prefetch buffer;
- processor loop;
- active handler tasks;
- SQL rows already claimed as fetched or processing.

## Shutdown Flow

1. The host cancellation token is triggered.
2. The fetcher stops claiming new SQL rows.
3. The prefetch channel writer completes.
4. The processor loop drains or releases prefetched rows.
5. Active processing tasks are given the configured grace period.
6. Jobs still processing after process exit rely on heartbeat/zombie recovery.

## Prefetched Rows

Fetched rows have not executed user code yet. On shutdown, ChokaQ tries to
release them back to pending. If release times out, abandoned-fetch recovery can
reset stale fetched rows later.

## Processing Rows

Processing rows may already have external side effects. They are not blindly
reset to pending. If the process dies and heartbeat stops, zombie rescue moves
them to DLQ for operator review.

## Configuration

| Setting | Purpose |
|---|---|
| `SqlServer.WorkerShutdownGracePeriod` | Time to wait for active processing tasks. |
| `SqlServer.PrefetchedJobReleaseTimeout` | Time budget for releasing a prefetched row. |
| `Recovery.FetchedJobTimeout` | Recovery timeout for stale fetched rows. |
| `Recovery.ProcessingZombieTimeout` | Recovery timeout for processing heartbeat expiry. |

## Architecture Decision

### Why this pattern?

Fetched work and processing work have different safety semantics. Fetched work
can be returned to pending. Processing work may have side effects and must go
through zombie/DLQ review if ownership is lost.

### Trade-offs

Graceful shutdown can delay host stop while processing tasks finish. A short
grace period improves deployment speed but creates more zombie review work.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Immediate process exit | Fast deployments. | More stale rows and ambiguous side effects. |
| Wait forever | Avoids interruption. | Can hang deployments. |
| Reset all rows to pending | Simple recovery. | Unsafe for processing side effects. |

### Interview questions

**Why not return processing rows to pending on shutdown?**  
Because user code may have already changed external systems.

**What happens to prefetched rows on shutdown?**  
The worker tries to release them. If it cannot, fetched-job recovery resets
them later.

**What is the production tuning knob?**  
`WorkerShutdownGracePeriod`, balanced against deployment speed and handler
duration.

