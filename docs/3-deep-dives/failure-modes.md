# Failure Modes

![Failure modes map](/diagrams/64-failure-modes-map.png)

This page groups the failures ChokaQ is designed to survive or expose.

## Failure Table

| Failure | ChokaQ behavior | Remaining responsibility |
|---|---|---|
| Host crashes before fetch | Job stays pending in `JobsHot`. | Monitor queue lag. |
| Host crashes after fetch before processing | Fetched recovery returns row to pending. | Tune fetched timeout. |
| Host crashes during processing | Heartbeat expires; zombie rescue moves row to DLQ. | Inspect side effects before resurrection. |
| Handler throws transient error | Job retries with delay if budget remains. | Classify exceptions accurately. |
| Handler throws fatal error | Job fast-fails to DLQ. | Fix code/payload. |
| SQL deadlock or transient outage | SQL retry policy retries storage operation. | Monitor SQL health. |
| SQL schema missing | Health check fails; workers cannot run correctly. | Run migrations/bootstrap. |
| SignalR disconnects | Dashboard can refresh from storage. | Do not treat SignalR as source of truth. |
| Operator bulk purges wrong rows | Data is gone. | Require authorization, preview, typed confirmation. |

## Worst Case To Understand

The hardest case is:

1. handler performs an external side effect;
2. process crashes before Archive;
3. recovery later makes the job executable again.

ChokaQ cannot know whether the external side effect happened. The handler must
use a business idempotency key or downstream provider idempotency.

## Architecture Decision

ChokaQ separates failures into two categories: failures the runtime can safely
repair automatically, and failures that must be surfaced for human or
application-level judgment. This distinction is the reason Fetched rows can be
returned to Pending while Processing zombies move to DLQ.

The alternative is to maximize automatic retry. That can look attractive in
load tests, but it hides the hard distributed-systems problem: a crashed handler
may have already called an external system. ChokaQ chooses explicit visibility
over pretending that the queue can guarantee exactly-once side effects across
arbitrary dependencies.

The trade-off is operational work. Teams must monitor DLQ, write idempotent
handlers, and define resurrection policy for business-critical jobs.

## Additional Questions

**Which failures are automatically safe to retry?**  
Fetched-but-not-started jobs and explicitly transient handler failures within
retry budget.

**Which failures require operator judgment?**  
Processing zombies, malformed payloads, exhausted retries, and destructive
operator actions.

**What is the main unsolved distributed systems problem?**  
Exactly-once external side effects across arbitrary systems.
