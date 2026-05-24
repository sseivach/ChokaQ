# Graceful Shutdown

![Graceful shutdown and prefetch release](/diagrams/48-graceful-shutdown-prefetch-release.png)

Graceful shutdown - это путь, который не дает prefetched и running work превратиться в неоднозначное состояние при остановке host.

ChokaQ разделяет shutdown behavior для:

- fetcher loop;
- local prefetch buffer;
- processor loop;
- active handler tasks;
- SQL rows, уже claim'нутых как fetched или processing.

## Shutdown flow

1. Host cancellation token срабатывает.
2. Fetcher перестает claim'ить новые SQL rows.
3. Prefetch channel writer завершается.
4. Processor loop drain'ит или release'ит prefetched rows.
5. Active processing tasks получают настроенный grace period.
6. Jobs, которые все еще processing после process exit, полагаются на heartbeat/zombie recovery.

## Prefetched rows

Fetched rows еще не выполняли user code. При shutdown ChokaQ пытается release'ить их обратно в pending. Если release истекает по timeout, abandoned-fetch recovery позже может reset'нуть stale fetched rows.

## Processing rows

Processing rows могли уже выполнить external side effects. Они не сбрасываются в pending автоматически. Если процесс умирает и heartbeat останавливается, zombie rescue перемещает их в DLQ для operator review.

## Configuration

| Setting | Purpose |
|---|---|
| `SqlServer.WorkerShutdownGracePeriod` | Время ожидания active processing tasks. |
| `SqlServer.PrefetchedJobReleaseTimeout` | Time budget для release prefetched row. |
| `Recovery.FetchedJobTimeout` | Recovery timeout для stale fetched rows. |
| `Recovery.ProcessingZombieTimeout` | Recovery timeout для processing heartbeat expiry. |

## Архитектурное решение

### Почему этот pattern?

Fetched work и processing work имеют разные safety semantics. Fetched work можно вернуть в pending. Processing work может иметь side effects и при потере ownership должен проходить zombie/DLQ review.

### Trade-offs

Graceful shutdown может задержать stop host, пока processing tasks завершаются. Короткий grace period ускоряет deployments, но создает больше zombie review work.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Immediate process exit | Быстрые deployments. | Больше stale rows и ambiguous side effects. |
| Wait forever | Избегает interruption. | Может повесить deployments. |
| Reset all rows to pending | Простое recovery. | Небезопасно для processing side effects. |

### Дополнительные вопросы

**Почему не возвращать processing rows в pending при shutdown?**  
Потому что user code мог уже изменить external systems.

**Что происходит с prefetched rows при shutdown?**  
Воркер пытается release'ить их. Если не получается, fetched-job recovery reset'ит их позже.

**Какой production tuning knob здесь главный?**  
`WorkerShutdownGracePeriod`, сбалансированный относительно deployment speed и handler duration.
