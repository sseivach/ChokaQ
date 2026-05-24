# The Deck Panel Guide

![The Deck dashboard](/screenshots/the-deck/dashboard.png)

The Deck - операционная поверхность ChokaQ. Он нужен в тот момент, когда queue уже не просто data structure, а живой production system с lag, failures, retries, stalled work и operator decisions.

Границы security описаны в [Authorization Model](/ru/4-the-deck/authorization-model). Безопасность mutations - в [Destructive Actions](/ru/4-the-deck/destructive-actions). Live updates - в [SignalR Notification Contract](/ru/4-the-deck/signalr-notification-contract).

## Dashboard areas

| Area | Purpose |
|---|---|
| Stats | Global throughput, success/failure counts и recent activity. |
| Health | Queue lag, worker liveness, SQL connectivity и saturation signals. |
| Queues | Per-queue pause state, max worker controls, zombie timeout controls. |
| Circuits | Circuit breaker state и visibility failure protection. |
| Job Matrix | Inspection active, archive и DLQ jobs. |
| Ops Panel | Payload inspection, edit, resurrection и bulk operations. |
| Console Log | Operator-visible runtime events и feedback admin actions. |

## Queue controls

Queue panel - не decoration. Он меняет runtime behavior.

| Control | Effect | Risk |
|---|---|---|
| Pause queue | Workers перестают fetch'ить новую work из этой queue. | Existing processing work может продолжиться. |
| Max workers | Ограничивает concurrent fetched/processing work per queue. | Слишком низко создает lag; слишком высоко может overload downstream. |
| Zombie timeout | Override global processing heartbeat timeout. | Слишком низко может отправить здоровые long-running jobs в DLQ. |

Operators должны относиться к queue controls как к production throttles.

## Job Matrix

![The Deck queue list](/screenshots/the-deck/queue-list.png)

Job Matrix - основная поверхность inspection.

Поведение history queries описано в [Paging, Sorting And Filtering](/ru/4-the-deck/paging-sorting-filtering).

| Source | What it shows | Typical action |
|---|---|---|
| Active | Pending, fetched, processing, delayed retry. | Confirm progress или cancel safe jobs. |
| Archive | Succeeded jobs. | Audit и confirm completion. |
| DLQ | Failed/cancelled/zombie jobs. | Inspect, edit, resurrect или purge. |

## Job details

![The Deck job details](/screenshots/the-deck/job-details.png)

Job details должны отвечать:

- что это за job type;
- какая queue им владеет;
- какой payload был сохранен;
- сколько attempts запускалось;
- когда job был created/scheduled/started/finished;
- какой worker им владел;
- почему job failed;
- безопасна ли resurrection.

## DLQ actions

![The Deck DLQ](/screenshots/the-deck/dlq.png)

| Action | Meaning | Safety requirement |
|---|---|---|
| Inspect | Прочитать failure reason и payload. | Safe. |
| Edit | Изменить payload/tags перед requeue. | Требует понимания handler contract. |
| Resurrect | Переместить DLQ row обратно в Hot. | Safe только если side effects idempotent. |
| Purge | Permanently delete DLQ row. | Destructive; требует confirmation и policy. |
| Bulk requeue | Requeue many DLQ rows by filter. | Требует tight filters и preview. |
| Bulk purge | Delete many DLQ rows by filter. | Самое рискованное operator action. |

## Operator workflow

1. Проверить health и queue lag.
2. Определить queue и type key, которые вызывают failure.
3. Открыть DLQ row.
4. Прочитать `FailureReason` до stack trace.
5. Решить, failure transient, permanent, stale, duplicate или zombie side-effect risk.
6. Исправить cause перед resurrection.
7. Resurrect small sample перед bulk actions.
8. Смотреть throughput и failure rate после requeue.

## Архитектурное решение

### Почему этот pattern?

Durable background work требует operator surface, потому что failure - не только developer concern. Когда jobs уже в SQL, системе нужны безопасные способы inspect и repair без прямого table editing.

### Trade-offs

Dashboard добавляет ответственность за authorization, destructive-action safety и UI correctness. Выигрыш в том, что production recovery больше не требует ad hoc SQL surgery.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| SQL-only operations | Не нужно поддерживать dashboard. | Error-prone и недоступно большинству operators. |
| Logs-only diagnosis | Легко добавить. | Logs не дают repair actions или current state. |
| External APM only | Хорошие metrics. | Не владеет job lifecycle decisions. |

### Дополнительные вопросы

**Зачем queue engine UI?**  
Потому что DLQ, pause, bulk requeue и circuit inspection - operational workflows, а не просто metrics.

**Как предотвращать accidental destructive actions?**  
Использовать authorization policies, typed confirmations, previews и bounded filters.

**Что если SignalR stale?**  
SignalR - notification layer. Storage остается authoritative; если есть сомнения, operators должны refresh/read committed state.
