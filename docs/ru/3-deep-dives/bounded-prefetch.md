# Bounded Prefetch

![Bounded prefetch capacity](/diagrams/26-bounded-prefetch-capacity.png)

Bounded prefetch - это pressure boundary между durable SQL backlog и local process memory. ChokaQ намеренно держит SQL worker prefetch buffer маленьким и bounded.

## Почему bounded важен

Unbounded local buffer может случайно превратить database-backed queue в memory-backed queue. Если worker claim'ит тысячи jobs, а потом замедляется, другие workers не видят эти rows, а owning process может исчерпать memory.

Bounded prefetch оставляет backlog в SQL.

## Capacity behavior

SQL worker использует bounded `Channel<JobHotEntity>`.

Когда в channel есть место:

- fetcher claim'ит due jobs из SQL;
- claimed rows записываются в buffer;
- processors потребляют rows по мере появления permits.

Когда channel full:

- fetcher waits;
- новые SQL rows не claim'ятся;
- backlog остается видимым в `JobsHot`.

## Operational meaning

Bounded prefetch дает operators более честную картину:

- если workers slow, queue lag растет в SQL;
- если SQL empty, но workers busy, buffer draining;
- если fetched rows stale, recovery может reset'ить их.

## Архитектурное решение

### Почему этот pattern?

Durable backlog должен оставаться в durable storage. Local process memory - только smoothing buffer, а не source of truth.

### Trade-offs

Маленький buffer может снизить peak throughput, когда SQL latency высока. Большой buffer увеличивает stale ownership и shutdown recovery work. Правильное значение - smallest buffer, который держит processors fed при normal latency.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Unbounded channel | Maximum short-term absorption. | Memory risk и hidden backlog. |
| No buffer | Проще correctness. | Больше SQL latency на execution path. |
| Per-queue buffers | Больше isolation. | Больше complexity и tuning surface. |

### Дополнительные вопросы

**Что защищает bounded prefetch?**  
Process memory, SQL visibility и fairness across workers.

**Может ли bounded prefetch снизить throughput?**  
Да, если он слишком мал для workload и SQL latency. Это tuning trade-off, а не correctness issue.

**Где должен жить backlog?**  
В `JobsHot`, потому что SQL durable и observable.
