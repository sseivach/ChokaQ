# SQL Query Reference

![Fetch next batch query flow](/diagrams/04-fetch-next-batch-query.png)

Эта страница объясняет runtime intent важных SQL operations. Она не заменяет `Queries.cs`; это архитектурный guide по набору queries.

## Fetch Next Batch

Caller: `SqlJobStorage.FetchNextBatchAsync` из `SqlJobWorker.FetcherLoopAsync`.

Intent: claim eligible pending rows из `JobsHot` без blocking других workers и без превышения per-queue worker budgets.

Tables touched:

- `JobsHot`
- `Queues`

Key behavior:

- `ActiveCounts` считает fetched/processing rows per queue.
- `Candidates` находит pending rows, которые due и не paused.
- `ROW_NUMBER()` ранжирует candidates внутри каждой queue.
- `UPDLOCK, READPAST` позволяет workers пропускать rows, которые уже claim'ятся.
- `UPDATE ... OUTPUT inserted.*` claim'ит rows и возвращает claimed data.

Failure mode: если eligible rows нет, worker sleeps на `PollingInterval`. Если SQL call transiently fails, storage retry policy может retry'ть operation.

## Mark As Processing

Caller: `JobProcessor` через `IJobStateManager`.

Intent: превратить fetched row в execution lease прямо перед запуском user code.

Tables touched:

- `JobsHot`

Key behavior:

- Устанавливает `Status = Processing`.
- Увеличивает `AttemptCount`.
- Устанавливает `StartedAtUtc`.
- Инициализирует `HeartbeatUtc`.
- Требует matching `WorkerId` и `Fetched` status, когда worker ownership передан.

Это последний stale-prefetch guard. Если row была released, reclaimed или changed после fetch, update затронет zero rows, и user code не запустится.

## Keep Alive

Caller: job heartbeat loop во время processing.

Intent: обновить `HeartbeatUtc`, чтобы zombie rescue знал, что job все еще alive.

Tables touched:

- `JobsHot`

Failure mode: repeated heartbeat write failures логируются и считаются. В зависимости от configuration execution может продолжиться или быть cancelled после threshold.

## Archive Succeeded

Caller: `JobProcessor` после успешного завершения handler.

Intent: atomically move completed job из active work в history.

Tables touched:

- `JobsHot`
- `JobsArchive`
- `StatsSummary`
- `MetricBuckets`

Key behavior:

- Starts короткую transaction с `XACT_ABORT ON`.
- Deletes exact row from `JobsHot`.
- Captures deleted data в table variable через `OUTPUT`.
- Inserts captured data в `JobsArchive`.
- Updates lifetime counters.
- Updates rolling metric buckets.
- Commits только если весь move успешен.

## Reschedule For Retry

Caller: `JobStateManager` после retryable failure или open circuit.

![Retry and DLQ query flow](/diagrams/05-retry-dlq-query-flow.png)

Intent: оставить job в `JobsHot`, вернуть его в pending и запланировать future retry.

Tables touched:

- `JobsHot`
- `StatsSummary`

Key behavior:

- Status становится pending.
- Worker ownership очищается.
- `ScheduledAtUtc` устанавливается в рассчитанное retry time.
- Retry counters обновляются для operator visibility.

## Move To DLQ

Caller: failure finalization, cancellation, zombie rescue и admin paths.

Intent: atomically remove job из active execution и сохранить его для operator inspection.

Tables touched:

- `JobsHot`
- `JobsDLQ`
- `StatsSummary`
- `MetricBuckets`

Pattern зеркалит Archive: delete from Hot, capture deleted row, insert into DLQ, update counters и metric buckets в той же transaction.

## Resurrect

Caller: The Deck или storage API.

Intent: atomically move DLQ job обратно в active work.

Tables touched:

- `JobsDLQ`
- `JobsHot`
- `StatsSummary`

Key behavior:

- Сначала deletes from `JobsDLQ`.
- Inserts into `JobsHot` со status pending и reset attempt count.
- Optional applies edited payload/tags/priority.
- Rolls back DLQ delete, если Hot insert fails.

## Recover Abandoned

Caller: `ZombieRescueService`.

Intent: release fetched rows, которые так и не дошли до processing.

Tables touched:

- `JobsHot`

Fetched rows еще не выполняли user code. Если они висят слишком долго, система может безопасно reset'нуть их в pending, очистив `WorkerId` и установив `Status = 0`.

## Archive Zombies

Caller: `ZombieRescueService`.

![Zombie rescue query flow](/diagrams/06-zombie-rescue-query-flow.png)

Intent: move processing rows с expired heartbeat в DLQ.

Tables touched:

- `JobsHot`
- `JobsDLQ`
- `StatsSummary`
- `MetricBuckets`
- `Queues`

Processing zombies не retry'ятся автоматически, потому что user code мог уже произвести side effects.

## Dashboard Reads

Dashboard queries используют bounded, purpose-specific reads:

| Query | Purpose |
|---|---|
| `GetSummaryStats` | Global lifetime counts. |
| `GetQueueStats` | Per-queue status counts and totals. |
| `GetQueueHealth` | Queue lag and pending saturation. |
| `GetThroughputStats` | Recent rate from `MetricBuckets`. |
| `GetTopDlqErrors` | Bounded recent DLQ error grouping. |
| `GetArchivePaged` | Committed archive page. |
| `GetDLQPaged` | Committed DLQ page. |

Telemetry reads могут использовать approximate committed snapshots там, где exactness не является частью safety contract. State transitions не полагаются на эти snapshots.

## Архитектурное решение

### Почему этот pattern?

Database - coordination boundary. Workers не координируются через process memory или distributed locks; они claim'ят rows через SQL state changes.

### Trade-offs

SQL coordination прозрачна и durable, но database становится central performance boundary. Поэтому index design, short transactions и bounded dashboard queries являются частью runtime contract.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Distributed lock service | Explicit lease control. | Дополнительная infrastructure и еще один failure mode. |
| Broker visibility timeout | Proven queue primitive. | Менее прямой SQL inspection и другая operational model. |
| App-level copy/delete | Легко написать. | Небезопасные cross-table moves при crash/failure. |

### Дополнительные вопросы

**Почему использовать `UPDATE ... OUTPUT` для fetch?**  
Потому что claiming и return exact claimed rows должны быть одной operation.

**Что защищает от stale prefetched work?**  
`MarkAsProcessing` проверяет worker ownership непосредственно перед dispatch.

**Dashboard `NOLOCK` reads используются для correctness?**  
Нет. Они только для observational views. Correctness обеспечивается state transition predicates и transactions.
