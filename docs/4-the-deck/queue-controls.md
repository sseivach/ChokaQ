# Queue Controls

![Per-queue runtime controls](/diagrams/47-per-queue-runtime-controls.png)

Queue controls let operators change runtime pressure without redeploying the
application.

The Deck exposes queue-level controls backed by the SQL `Queues` table.

Queue controls are destructive runtime mutations. For the authorization and validation model, see [Authorization Model](/4-the-deck/authorization-model) and [Destructive Actions](/4-the-deck/destructive-actions).

## Controls

| Control | Runtime effect |
|---|---|
| Pause queue | Fetcher stops claiming new work from the queue. |
| Max workers | Fetch query caps fetched/processing work for that queue. |
| Zombie timeout | Overrides global processing heartbeat timeout for the queue. |
| Active flag | Marks whether queue participates in runtime selection. |

## Pause Semantics

Pause prevents new fetches. It does not necessarily stop jobs already in
processing. Processing jobs continue unless cancelled or the host stops.

This distinction avoids pretending that pause can roll back side effects.

## Max Worker Semantics

`MaxWorkers` is enforced in the SQL fetch query by counting current fetched and
processing rows and ranking candidates within each queue.

This protects downstream systems from one queue consuming all available
execution capacity.

## Zombie Timeout Semantics

Queue-specific zombie timeout is useful when queues have very different handler
durations. A fast webhook queue and a long report-generation queue should not
necessarily share the same processing timeout.

## Architecture Decision

### Why this pattern?

Operational control should not require deployment. Queue pause and worker caps
are production throttles.

### Trade-offs

Runtime controls can be dangerous if operators do not understand their effect.
The Deck must show state clearly and audit/destructive operations separately.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Config-file only | Change-controlled. | Requires restart/redeploy. |
| Global worker count only | Simple. | Cannot isolate a bad queue. |
| Separate worker pools per queue | Strong isolation. | More infrastructure and tuning. |

### Additional Questions

**Does pause stop running work?**  
No. It stops new fetches. Running handlers must finish or be cancelled.

**Why cap workers per queue?**  
To prevent one queue or downstream dependency from consuming the whole worker
budget.

**What is dangerous about zombie timeout?**  
Too low a value can mark healthy long-running jobs as zombies.
