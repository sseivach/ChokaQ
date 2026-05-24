# Queue Controls

![Per-queue runtime controls](/diagrams/47-per-queue-runtime-controls.png)

Queue controls позволяют operators менять runtime pressure без redeploy application.

The Deck показывает queue-level controls, backed by SQL table `Queues`.

Queue controls - destructive runtime mutations. Authorization и validation model описаны в [Authorization Model](/ru/4-the-deck/authorization-model) и [Destructive Actions](/ru/4-the-deck/destructive-actions).

## Controls

| Control | Runtime effect |
|---|---|
| Pause queue | Fetcher перестает claim'ить new work из queue. |
| Max workers | Fetch query ограничивает fetched/processing work для этой queue. |
| Zombie timeout | Override global processing heartbeat timeout для queue. |
| Active flag | Указывает, участвует ли queue в runtime selection. |

## Pause semantics

Pause предотвращает new fetches. Он не обязательно останавливает jobs, которые уже processing. Processing jobs продолжаются, если их не cancelled или host не stopping.

Это различие важно: pause не может rollback'нуть side effects.

## Max worker semantics

`MaxWorkers` enforced в SQL fetch query через count current fetched и processing rows и ranking candidates внутри каждой queue.

Это защищает downstream systems от ситуации, где одна queue потребляет всю execution capacity.

## Zombie timeout semantics

Queue-specific zombie timeout полезен, когда queues имеют очень разную handler duration. Fast webhook queue и long report-generation queue не обязаны иметь одинаковый processing timeout.

## Архитектурное решение

### Почему этот pattern?

Operational control не должен требовать deployment. Queue pause и worker caps - production throttles.

### Trade-offs

Runtime controls могут быть опасны, если operators не понимают их effect. The Deck должен ясно показывать state и отделять audit/destructive operations.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Config-file only | Change-controlled. | Требует restart/redeploy. |
| Global worker count only | Просто. | Нельзя isolate bad queue. |
| Separate worker pools per queue | Strong isolation. | Больше infrastructure и tuning. |

### Дополнительные вопросы

**Pause останавливает running work?**  
Нет. Он останавливает new fetches. Running handlers должны finish или быть cancelled.

**Зачем cap workers per queue?**  
Чтобы одна queue или downstream dependency не съела весь worker budget.

**Чем опасен zombie timeout?**  
Слишком низкое значение может пометить здоровые long-running jobs как zombies.
