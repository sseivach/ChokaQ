# Dead Letter Queue

The Dead Letter Queue is the controlled failure boundary in ChokaQ. A job enters
DLQ when the runtime decides that normal processing should stop and an operator
or developer needs to inspect the failure.

![DLQ lifecycle](/diagrams/29-dlq-lifecycle.png)

## One-Minute Explanation

DLQ is not a discard path. It is an inspection and recovery table for work that
could not safely finish through the normal worker path.

In ChokaQ, failed jobs leave `JobsHot` and move to `JobsDLQ` with their payload,
type key, queue, attempts, worker information, failure reason, and error details.
The Deck can then show the row, let an operator inspect the payload and stack
trace, and optionally resurrect the job back into Hot.

## When Jobs Enter DLQ

| Path | Trigger | Why DLQ is safer than retry |
| --- | --- | --- |
| Fatal failure | Smart Worker classifies an exception as non-retryable | Repeating code or payload failures increases queue lag without changing the root cause |
| Max retries exceeded | Transient retries are exhausted | The dependency or handler did not recover in time |
| Zombie processing job | Heartbeat expires while job is Processing | Side effects may have partially completed |
| Manual operator action | Admin cancels or isolates failed work | Human decision is required before retry |

## Storage Role

DLQ is the third pillar in the Hot, Archive, DLQ model:

| Table | Purpose |
| --- | --- |
| `JobsHot` | Pending, fetched, and processing work |
| `JobsArchive` | Terminal success, cancellation, and retained completion history |
| `JobsDLQ` | Terminal failure requiring inspection or controlled resurrection |

Moving a job to DLQ is a terminal state transition. The job is removed from Hot
so workers will not keep claiming it, and it becomes visible to The Deck as
failed work.

## Failure Reasons

DLQ rows should explain why normal processing stopped. The important distinction
is not only "failed" but "what kind of failure happened?"

| Reason family | Typical source | Operator response |
| --- | --- | --- |
| Fatal | `ArgumentException`, `JsonException`, `ChokaQFatalException` | Fix code or payload before resurrection |
| Retries exhausted | Timeout, temporary downstream failure, HTTP failure | Check dependency recovery, then bulk resurrect if safe |
| Zombie | Worker crash, host eviction, heartbeat loss | Verify idempotency and downstream state before resurrection |
| Unknown | Unexpected runtime/storage failure | Inspect logs and stack trace before touching the job |

## The Deck Visibility

The Deck should make DLQ work clear and repeatable:

- queue and job type;
- failure reason;
- attempt count;
- last worker id;
- failed timestamp;
- payload and tags;
- exception summary and stack trace;
- safe actions: inspect, edit, resurrect, purge.

The operator's first question should be "why is this job here?" The second
question should be "is it safe to run again?"

## Resurrection Boundary

Resurrection moves a DLQ row back to `JobsHot` as Pending work. That is a
deliberate boundary: ChokaQ does not process rows directly from DLQ.

That decision keeps one execution path:

1. Hot row is fetched.
2. Worker starts processing.
3. Heartbeat tracks liveness.
4. Handler runs through middleware.
5. Result goes to Archive, retry, or DLQ.

If resurrection bypassed Hot, every lifecycle rule would need a second version.

## Production Tuning

Monitor DLQ by rate, age, and reason:

| Signal | Meaning |
| --- | --- |
| DLQ rate rising | New deployment, dependency outage, or bad input stream |
| Old DLQ rows | Operators are not closing failed work |
| One job type dominates | Handler-specific bug or downstream contract break |
| Zombie reason dominates | Worker stability, heartbeat, or host shutdown issue |
| Fatal reason dominates | Payload validation or code correctness issue |

DLQ growth is usually not solved by adding workers. More workers can process bad
jobs faster, but they cannot make bad payloads or broken handlers succeed.

## Architecture Decision

ChokaQ treats DLQ as a first-class lifecycle state because failed background
work needs more than a log line. A database row gives operators enough context
to investigate, preserves payload evidence, and provides a controlled recovery
path.

The alternative is to discard failed work after logging or to retry forever.
Discarding loses business operations. Retrying forever hides real defects and
creates retry storms. DLQ is the middle path: stop automatic execution, preserve
evidence, and require a deliberate next action.

The trade-off is operational responsibility. A DLQ only helps if teams review it
and have clear ownership for resurrection, purge, or bug fixes.

## Additional Questions

**Why is DLQ separate from Archive?**  
Archive is retained completion history. DLQ is unresolved failed work that may
need correction and resurrection. Mixing them weakens both read models.

**Why not automatically resurrect max-retry jobs later?**  
Because exhausting retries means the normal recovery policy already failed.
Automatic resurrection without new evidence can restart the same failure loop.

**What makes DLQ safe for enterprise operations?**  
Clear failure reasons, immutable context unless explicitly edited, audited
operator actions, and a resurrection path that returns jobs to the normal Hot
lifecycle.

> Next: review [Retry And DLQ](/2-lifecycle/retry-and-dlq) for the retry path and [Edit + Resurrect](/4-the-deck/resurrect-dlq) for operator recovery.
