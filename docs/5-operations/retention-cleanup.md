# Retention Cleanup

![Retention cleanup](/diagrams/51-retention-cleanup.png)

Retention cleanup prevents history tables from growing forever while preserving
enough evidence for operations, audit, and incident response.

Cleanup is intentionally separate from worker execution. Active work must stay
fast and predictable.

## What Can Be Cleaned

| Data | Cleanup meaning |
|---|---|
| `JobsArchive` | Remove old successful history after retention window. |
| `JobsDLQ` | Remove failed/recovery evidence only after explicit retention policy. |
| `MetricBuckets` | Remove old rolling observability buckets after they are no longer useful. |

`JobsHot` is active work and should not be cleaned by retention policy.

## Batch Size

`SqlServer.CleanupBatchSize` bounds cleanup delete operations. Large one-shot
deletes can hold locks, grow the transaction log, and interfere with dashboard
queries.

Batch cleanup gives operators a predictable pressure envelope.

## DLQ Retention Is Different

DLQ rows are not just old data. They are recovery evidence. Purging DLQ too
early can destroy the only copy of a failed payload and error reason.

Production teams should define:

- minimum DLQ retention;
- who can purge;
- whether bulk purge requires approval;
- whether DLQ payloads contain sensitive data;
- whether failed payloads must be exported before deletion.

## Architecture Decision

### Why this pattern?

Retention is an operational policy, not a worker correctness decision. Keeping
cleanup batched and explicit avoids turning normal processing into accidental
history management.

### Trade-offs

Long retention increases storage cost and dashboard history volume. Short
retention reduces investigation ability.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Keep everything forever | Maximum evidence. | Unbounded storage and compliance risk. |
| Purge aggressively | Small database. | Weak incident analysis and recovery. |
| Archive externally | Keeps SQL smaller. | Adds data pipeline and split investigation surface. |

### Interview questions

**Why batch cleanup?**  
To bound locks, log growth, and operational impact.

**Why treat DLQ differently from Archive?**  
DLQ may contain unresolved business work and recovery evidence.

**Should cleanup touch active jobs?**  
No. Active lifecycle policy and retention policy are separate.

