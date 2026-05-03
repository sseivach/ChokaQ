# Rolling Observability Buckets

This chapter documents an intentional design upgrade in ChokaQ's dashboard
observability. It is written as an architecture lesson: start with the simpler
version, understand why it was reasonable, then move to the production-grade
version once its limits become visible.

## The Teaching Pattern

A good enterprise system does not need to begin with the most sophisticated
version of every feature. In fact, learning is often better when the project
shows the evolution:

1. Build the simplest honest version.
2. Explain what guarantee it provides.
3. Measure where it becomes weak.
4. Replace it with a stronger design.
5. Document the trade-off, not just the final code.

ChokaQ uses that pattern here. The first throughput implementation was a bounded
lookback over `JobsArchive` and `JobsDLQ`. The new implementation records
completion outcomes into `MetricBuckets` during the same transaction that moves a
job to Archive or DLQ.

## Version 1: Bounded History Lookback

The first implementation answered recent throughput and failure rate directly
from lifecycle tables:

```sql
SELECT
    COUNT_BIG(*)
FROM [chokaq].[JobsArchive]
WHERE [FinishedAtUtc] >= @OneMinuteCutoff;

SELECT
    COUNT_BIG(*)
FROM [chokaq].[JobsDLQ]
WHERE [FailedAtUtc] >= @OneMinuteCutoff;
```

This was not a bad design. It was a good production-preview step because it was:

- easy to reason about;
- based on the canonical job state tables;
- parameterized and bounded by time;
- able to use date indexes;
- implemented without introducing another write path.

For a small or early system, this version is often exactly where you should
start. It gives real signals without inventing metrics infrastructure too early.

## Where Version 1 Breaks Down

The bounded lookback has several limits:

| Problem | Why It Matters |
|---|---|
| History tables become metrics tables | Archive and DLQ are optimized for investigation and retention, not constant dashboard rate reads. |
| Query cost grows with retention pressure | Even indexed recent windows still couple dashboard cost to lifecycle table shape and size. |
| Requeue can rewrite recent history | If a DLQ row is resurrected, direct DLQ counting can make the failure disappear from the failure-rate window. |
| Purge affects metrics | Operational cleanup should not erase the fact that failures happened. |
| More metrics become awkward | Duration histograms, outcome families, and longer charts become hard without repeatedly scanning job history. |

The most important semantic issue is requeue. A job can fail, land in DLQ, get
fixed by an operator, and be moved back to Hot. The DLQ row is gone, but the
failure event still happened. Failure rate should describe recent processing
outcomes, not the current contents of the DLQ table.

## Version 2: MetricBuckets

The stronger design separates lifecycle history from rolling observability:

```text
Hot -> Archive transaction
    writes JobsArchive
    increments StatsSummary
    increments MetricBuckets

Hot -> DLQ transaction
    writes JobsDLQ
    increments StatsSummary
    increments MetricBuckets
```

`MetricBuckets` stores small aggregate rows:

```sql
CREATE TABLE [chokaq].[MetricBuckets](
    [BucketStartUtc]  datetime2(0) NOT NULL,
    [Queue]           varchar(255) NOT NULL,
    [Outcome]         tinyint      NOT NULL,
    [FailureReason]   int          NOT NULL,
    [CompletedCount]  bigint       NOT NULL,
    [DurationCount]   bigint       NOT NULL,
    [TotalDurationMs] float        NOT NULL,
    [MaxDurationMs]   float        NULL,
    [LastUpdatedUtc]  datetime2(7) NOT NULL
);
```

The bucket is currently one UTC second. That is precise enough for 1-minute and
5-minute dashboard windows without storing one metrics row per job.

## Why The New Version Is Better

The dashboard now reads this shape:

```sql
SELECT
    SUM(CASE WHEN [BucketStartUtc] >= @OneMinuteCutoff
             THEN [CompletedCount] ELSE 0 END) AS ProcessedLastMinute,
    SUM(CASE WHEN [BucketStartUtc] >= @OneMinuteCutoff AND [Outcome] = 1
             THEN [CompletedCount] ELSE 0 END) AS FailedLastMinute,
    SUM([CompletedCount]) AS ProcessedLastFiveMinutes,
    SUM(CASE WHEN [Outcome] = 1
             THEN [CompletedCount] ELSE 0 END) AS FailedLastFiveMinutes
FROM [chokaq].[MetricBuckets]
WHERE [BucketStartUtc] >= @FiveMinuteCutoff;
```

The advantages are:

- The dashboard reads tiny time buckets instead of lifecycle history.
- Requeue and purge no longer erase recent outcome events.
- The cost of throughput/failure-rate reads is tied to bucket count, not Archive
  or DLQ retention.
- The write is transactionally coupled to the final state transition.
- Future charts can reuse the same bucket table.
- Duration aggregates are available without rescanning Archive.

## Trade-Offs

This design is stronger, but not free:

| Cost | Mitigation |
|---|---|
| More write work on finalization | The bucket update happens in the same SQL transaction and groups rows by queue/outcome. |
| More schema surface area | The table has a clear single responsibility: rolling operational aggregates. |
| Possible hot bucket contention | Second-level buckets reduce contention compared with minute buckets while keeping reads small. |
| Existing history needs backfill if upgraded in place | ChokaQ treats new buckets as the source of truth from the moment the schema is installed. A future migration can backfill if needed. |

The enterprise lesson is not "always start with buckets." The lesson is:
**start with the simplest correct signal, then graduate when correctness,
operational cost, or semantics demand a better boundary.**

## Separation Of Concerns

ChokaQ now uses three different read models for three different questions:

| Question | Source |
|---|---|
| How many jobs have ever succeeded or failed? | `StatsSummary` |
| Which jobs succeeded or failed, and why? | `JobsArchive` / `JobsDLQ` |
| How fast is the system processing right now? | `MetricBuckets` |

That separation is the real architectural improvement. Each table now has a job,
and the dashboard no longer asks one table to answer every operational question.
