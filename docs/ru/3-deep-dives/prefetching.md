# Prefetching

![Prefetch worker pipeline](/diagrams/25-prefetch-worker-pipeline.png)

Prefetching отделяет SQL polling от handler execution. SQL worker fetch'ит небольшие batches из `JobsHot` в local buffer, затем processors потребляют этот buffer под dynamic concurrency limiter.

Это улучшает throughput, не превращая SQL Server в tight per-job round-trip bottleneck.

## Где это находится

SQL prefetching живет в `SqlJobWorker`.

У worker есть два loop:

| Loop | Responsibility |
|---|---|
| Fetcher loop | Claim eligible rows из SQL и пишет их в local prefetch buffer. |
| Processor loop | Читает prefetched rows, ждет concurrency permit, validates execution lease, затем dispatch'ит user code. |

## Happy path

1. Fetcher проверяет queue configuration.
2. Fetcher claim'ит due pending jobs через `FetchNextBatch`.
3. Claimed rows попадают в bounded channel.
4. Processor читает row из channel.
5. Processor ждет concurrency capacity.
6. Processor вызывает `MarkAsProcessing`.
7. Handler выполняется.
8. Job перемещается в Archive, retry schedule или DLQ.

## Stale prefetch protection

Prefetched work недостаточно для запуска user code. Перед dispatch handler'а ChokaQ выполняет `MarkAsProcessing`. Этот SQL update проверяет, что row все еще принадлежит worker и все еще находится в expected fetched state.

Если row была released, paused, reclaimed или moved, `MarkAsProcessing` затронет zero rows, и user code не запустится.

## Failure path

Если host shuts down или queue pauses, пока rows prefetched, worker release'ит rows обратно в pending, где это возможно. Если release timeout'ится, abandoned-fetch recovery может reclaim'ить их позже, потому что fetched rows еще не выполняли user code.

## Архитектурное решение

### Почему этот pattern?

Без prefetching каждый handler slot был бы связан с SQL polling latency. С prefetching SQL может claim'ить work небольшими batches, пока execution идет параллельно.

### Trade-offs

Prefetching создает временное local ownership window. ChokaQ компенсирует это bounded buffer, release-on-shutdown behavior, abandoned-fetch recovery и final `MarkAsProcessing` lease gate.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Fetch one job per handler slot | Проще. | Больше SQL round trips и слабее throughput при latency. |
| Fetch huge batches | Меньше SQL calls. | Больше stale ownership и memory risk. |
| Broker push model | Отлично для части workloads. | Другая infrastructure и менее прямое SQL operational state. |

### Дополнительные вопросы

**Главный риск prefetching?**  
Worker может держать jobs локально, не начав их. ChokaQ ограничивает этот риск small channel и validates lease before execution.

**Почему fetch не считается attempt?**  
Потому что user code еще не запускался. Attempts увеличиваются при переходе row в `Processing`.

**Что будет, если prefetched job так и не processed?**  
Fetched-job recovery reset'ит stale fetched rows обратно в pending.
