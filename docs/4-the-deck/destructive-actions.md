# Destructive Actions

![Destructive actions safety](/diagrams/55-destructive-actions-safety.png)

Destructive actions are operations that mutate job state, queue state, or stored
evidence. They need stronger safety rules than normal dashboard reads.

## Destructive Commands

Examples:

- cancel job;
- restart job;
- resurrect DLQ job;
- repair and requeue DLQ job;
- edit payload/tags/priority;
- purge DLQ rows;
- bulk cancel;
- bulk restart;
- bulk requeue by filter;
- bulk purge by filter;
- pause/resume queue;
- change queue timeout;
- change max workers.

## Safety Layers

| Layer | Purpose |
|---|---|
| Hub input validation | Reject malformed IDs, queue names, payloads, tags, priorities, and filters. |
| Destructive authorization policy | Require stronger privilege for mutation. |
| Batch size caps | Prevent huge accidental operations. |
| JSON validation | Prevent edited payloads from becoming poison pills. |
| Filter normalization | Keep custom SignalR clients within supported bounds. |
| Typed confirmation in UI | Force operator intent before high-impact actions. |
| Logs with actor | Preserve audit evidence. |

## Bulk Operations

Bulk operations should always be treated as incident-grade actions. A safe flow
is:

1. Filter narrowly.
2. Preview count and scope.
3. Confirm typed token.
4. Execute bounded batch.
5. Refresh history page and stats.
6. Watch failure rate and queue lag.

## Architecture Decision

### Why this pattern?

The Deck needs repair power during incidents, but repair power can destroy data
or amplify failure. Safety must exist at both UI and hub boundaries.

### Trade-offs

Extra confirmation slows expert operators. That is intentional for irreversible
actions like purge.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| UI-only validation | Good UX. | Custom clients can bypass it. |
| Hub-only validation | Strong boundary. | Worse UX without previews/confirmations. |
| No destructive actions | Safer surface. | Operators fall back to dangerous ad hoc SQL. |

### Interview questions

**What is the riskiest action?**  
Bulk purge, because it permanently destroys recovery evidence.

**Why validate JSON in the hub?**  
To prevent storing edited payloads that cannot be dispatched later.

**Why log actor identity?**  
Operator actions are part of incident evidence and audit.

