# Rolling Observability Buckets

Эта глава описывает, как ChokaQ считает recent throughput и failure-rate signals для The Deck.

Текущая реализация записывает completion outcomes в `MetricBuckets` во время той же transaction, которая перемещает job в Archive или DLQ. Такой design держит dashboard reads bounded и избегает scanning growing history tables при каждом refresh.

![Rolling observability buckets](/diagrams/38-rolling-observability-buckets.png)

## Version 1: bounded history lookback

Первая реализация отвечала на recent throughput и failure rate напрямую из lifecycle tables:

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

Это был разумный production-preview step, потому что он:

- easy to reason about;
- based on canonical job state tables;
- parameterized и bounded by time;
- мог использовать date indexes;
- implemented без introduction another write path.

Для small или early system такая version часто именно то, с чего нужно начинать. Она дает real signals без premature metrics infrastructure.

## Где Version 1 перестает хватать

Bounded lookback имеет несколько limits:

| Problem | Why It Matters |
|---|---|
| History tables become metrics tables | Archive и DLQ оптимизированы под investigation и retention, а не constant dashboard rate reads. |
| Query cost grows with retention pressure | Даже indexed recent windows связывают dashboard cost с shape и size lifecycle tables. |
| Requeue can rewrite recent history | Если DLQ row resurrected, direct DLQ counting может заставить failure исчезнуть из failure-rate window. |
| Purge affects metrics | Operational cleanup не должен стирать факт, что failures happened. |
| More metrics become awkward | Duration histograms, outcome families и longer charts сложно делать без repeated scanning job history. |

Самая важная semantic issue - requeue. Job может fail, попасть в DLQ, быть fixed operator'ом и вернуться в Hot. DLQ row исчезла, но failure event happened. Failure rate должен описывать recent processing outcomes, а не current contents of DLQ table.

## Version 2: MetricBuckets

Следующий design разделяет lifecycle history и rolling observability:

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

`MetricBuckets` хранит small aggregate rows:

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

Bucket сейчас равен одной UTC second. Этого достаточно для 1-minute и 5-minute dashboard windows без хранения one metrics row per job.

## Почему MetricBuckets стали следующим шагом

Dashboard теперь читает такую shape:

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

Практические benefits:

- Dashboard читает tiny time buckets вместо lifecycle history.
- Requeue и purge больше не стирают recent outcome events.
- Стоимость throughput/failure-rate reads привязана к bucket count, а не к Archive или DLQ retention.
- Write transactionally coupled к final state transition.
- Future charts могут reuse эту же bucket table.
- Duration aggregates доступны без rescanning Archive.

## Trade-offs

Этот design улучшает operational read model, но не бесплатен:

| Cost | Mitigation |
|---|---|
| More write work on finalization | Bucket update происходит в той же SQL transaction и groups rows by queue/outcome. |
| More schema surface area | Table имеет clear single responsibility: rolling operational aggregates. |
| Possible hot bucket contention | Second-level buckets уменьшают contention по сравнению с minute buckets, сохраняя reads small. |
| Existing history needs backfill if upgraded in place | ChokaQ treats new buckets as source of truth с момента schema installation. Future migration может backfill if needed. |

Enterprise lesson не в том, что нужно always start with buckets. Урок такой: **start with the simplest correct signal, then graduate when correctness, operational cost, or semantics demand a better boundary.**

## Separation of concerns

ChokaQ теперь использует три read models для трех разных questions:

| Question | Source |
|---|---|
| How many jobs have ever succeeded or failed? | `StatsSummary` |
| Which jobs succeeded or failed, and why? | `JobsArchive` / `JobsDLQ` |
| How fast is the system processing right now? | `MetricBuckets` |

Это и есть реальное architectural improvement. У каждой table теперь есть job, и dashboard больше не заставляет одну table отвечать на каждый operational question.

## Архитектурное решение

ChokaQ держит rolling observability отдельно от lifecycle history, потому что questions разные. Archive и DLQ tables отвечают "что случилось с этим job?" Metrics buckets отвечают "что система делает прямо сейчас?" Смешивание этих concerns заставляет dashboard refreshes scan tables, чья главная цель - audit и investigation.

Выбранный design записывает tiny aggregate event в той же точке, где job достигает terminal state. Это сохраняет outcome, даже если operator позже resurrects или purges DLQ row. Recent failure rate остается фактом о recent execution, а не side effect current DLQ contents.

Trade-off - дополнительный write во время finalization. ChokaQ принимает это, потому что finalization уже является transactional boundary, а bounded dashboard reads важнее, чем избегание одного small aggregate update.

## Дополнительные вопросы

**Почему не считать dashboard rates напрямую из Archive и DLQ forever?**  
Потому что эти tables растут с retention и operator workflows. Requeue и purge также могут менять их contents после того, как failure event happened.

**Почему buckets пишутся в terminal-state transaction?**  
Чтобы lifecycle state и operational metrics не расходились из-за partial write. Если job дошел до Archive или DLQ, corresponding rolling outcome записывается вместе с ним.

**Что менять на очень high scale?**  
Potential next steps: coarser bucket partitions, asynchronous metrics export или database-specific upsert tuning. Current design держит первый production model simple и query-bounded.
