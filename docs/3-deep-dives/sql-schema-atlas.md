# SQL Schema Atlas

![SQL schema map](/diagrams/03-sql-schema-map.png)

ChokaQ SQL Server storage is built around active work, immutable history,
operator recovery, lifetime counters, rolling metrics, queue configuration, and
schema migration metadata.

The default schema name is `chokaq`.

## Tables

| Table | Purpose | Hot path? |
|---|---|---|
| `JobsHot` | Active work: pending, fetched, processing, delayed retry. | Yes |
| `JobsArchive` | Successful completed jobs. | No |
| `JobsDLQ` | Failed, cancelled, zombie, or operator-held jobs. | No |
| `StatsSummary` | Lifetime counters per queue. | No |
| `MetricBuckets` | Recent throughput and failure-rate aggregates. | No |
| `Queues` | Queue runtime configuration. | Yes, read by workers |
| `SchemaMigrations` | Applied ChokaQ SQL schema versions. | Startup/ops |

## `JobsHot`

`JobsHot` is the only table workers scan to find executable work. Keeping it
small is the core performance decision behind the Three Pillars model.

Key columns:

| Column | Meaning |
|---|---|
| `Id` | Stable job identifier. |
| `Queue` | Queue partition and operational control boundary. |
| `Type` | Persisted job type key. |
| `Payload` | Serialized job payload. |
| `Tags` | Optional operator/search metadata. |
| `IdempotencyKey` | Optional active-work dedupe key. |
| `Priority` | Higher values are fetched first. |
| `Status` | Pending, fetched, or processing. |
| `AttemptCount` | Number of executions that reached `Processing`. |
| `ScheduledAtUtc` | Future eligibility time for delays and retries. |
| `WorkerId` | Current worker owner after fetch. |
| `HeartbeatUtc` | Processing liveness signal. |

Important indexes:

| Index | Supports |
|---|---|
| `IX_JobsHot_Fetch` | Worker fetch ordering by queue, priority, schedule, creation time. |
| `IX_JobsHot_Idempotency` | Unique active idempotency key. |
| `IX_JobsHot_QueueStats` | Queue dashboard counts. |
| `IX_JobsHot_PendingLag` | Queue lag health checks. |
| `IX_JobsHot_StatusCreated` | Active job dashboard view. |
| `IX_JobsHot_FetchedRecovery` | Abandoned fetched-job recovery. |
| `IX_JobsHot_ProcessingHeartbeat` | Zombie detection. |

## `JobsArchive`

`JobsArchive` stores succeeded jobs after a successful final transition. It is
not part of the worker fetch path.

Indexes support recent history, queue-specific history, and tag search. Archive
can grow independently from active work because workers do not scan it for new
jobs.

## `JobsDLQ`

`JobsDLQ` stores failed, cancelled, zombie, and operator-held jobs. It powers
inspection, edit, resurrection, bulk requeue, bulk purge, and failure grouping.

Important indexes:

| Index | Supports |
|---|---|
| `IX_JobsDLQ_Date` | Recent DLQ view. |
| `IX_JobsDLQ_Queue` | Queue-scoped DLQ inspection. |
| `IX_JobsDLQ_Reason` | Failure taxonomy filtering. |
| `IX_JobsDLQ_Type` | Type-key failure triage. |
| `IX_JobsDLQ_CreatedAt` | Age-based cleanup and investigation. |

## `StatsSummary`

`StatsSummary` stores lifetime counters per queue:

- succeeded total;
- failed total;
- retried total;
- last activity timestamp.

It avoids recomputing lifetime counters by scanning Archive and DLQ.

## `MetricBuckets`

`MetricBuckets` stores recent completion aggregates. ChokaQ updates buckets
inside the same transaction that moves a job to Archive or DLQ.

This makes dashboard throughput cheap and stable:

- Archive answers investigation questions;
- DLQ answers recovery questions;
- `MetricBuckets` answers recent-rate questions.

## `Queues`

`Queues` is the runtime control table. It stores:

- queue name;
- paused/active flags;
- per-queue zombie timeout;
- optional max worker budget;
- last update timestamp.

Workers read this table to avoid fetching paused queues and to enforce
per-queue capacity.

## `SchemaMigrations`

`SchemaMigrations` records applied ChokaQ schema versions. It turns first-start
bootstrap into an auditable operation instead of relying only on idempotent
`CREATE TABLE IF MISSING` logic.

## Architecture Decision

### Why this pattern?

The schema separates active work from historical evidence. That keeps worker
queries small while preserving completed and failed jobs for operators.

### Trade-offs

Moving between tables requires transaction integrity. The implementation must
be careful to avoid copy-then-delete bugs. ChokaQ uses short SQL transactions
and `OUTPUT` to make those moves atomic.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Single `Jobs` table with status column | Simple model. | Hot path degrades as history grows. |
| Broker-only queue | High throughput. | Less transparent operational state without extra storage. |
| Archive outside SQL | Smaller database. | Harder debugging and split-brain evidence. |

### Additional Questions

**Why not keep every job in one table?**  
Because active fetch queries would share indexes and storage with years of
history, making the worker path sensitive to retention.

**Why materialize `MetricBuckets`?**  
Because dashboard rate windows should not scan mutable history tables or change
when an operator purges/requeues DLQ rows.

**What is the main risk of this schema?**  
Cross-table moves must be atomic. That is why final transitions use database
transactions and deleted-row capture.
