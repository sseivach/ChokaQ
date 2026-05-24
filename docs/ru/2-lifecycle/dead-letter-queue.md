# Dead Letter Queue

Dead Letter Queue - это контролируемая граница отказа в ChokaQ. Задание попадает в DLQ, когда runtime решает, что обычную обработку нужно остановить, а failure должен проверить оператор или разработчик.

![DLQ lifecycle](/diagrams/29-dlq-lifecycle.png)

## Объяснение за минуту

DLQ - это не путь выбрасывания данных. Это таблица inspection и recovery для работы, которая не смогла безопасно завершиться через нормальный worker path.

В ChokaQ failed jobs выходят из `JobsHot` и перемещаются в `JobsDLQ` вместе с payload, type key, queue, attempts, worker information, failure reason и error details. The Deck может показать строку, дать оператору посмотреть payload и stack trace, а при необходимости resurrect'ить задание обратно в Hot.

## Когда задания попадают в DLQ

| Path | Trigger | Почему DLQ безопаснее retry |
| --- | --- | --- |
| Fatal failure | Smart Worker классифицирует exception как non-retryable | Повторение code или payload failures увеличивает queue lag, но не меняет root cause |
| Max retries exceeded | Transient retries исчерпаны | Dependency или handler не восстановились вовремя |
| Zombie processing job | Heartbeat истек, пока задание было в Processing | Side effects могли быть частично выполнены |
| Manual operator action | Admin отменяет или изолирует failed work | Перед retry нужно человеческое решение |

## Роль storage

DLQ - третий pillar в модели Hot, Archive, DLQ:

| Table | Purpose |
| --- | --- |
| `JobsHot` | Pending, fetched и processing work |
| `JobsArchive` | Terminal success, cancellation и retained completion history |
| `JobsDLQ` | Terminal failure, требующий inspection или controlled resurrection |

Перемещение задания в DLQ - это terminal state transition. Задание удаляется из Hot, чтобы воркеры перестали его claim'ить, и становится видимым для The Deck как failed work.

## Failure reasons

Строки DLQ должны объяснять, почему нормальная обработка остановилась. Важное различие не только "failed", а "какой именно тип failure произошел".

| Reason family | Typical source | Operator response |
| --- | --- | --- |
| Fatal | `ArgumentException`, `JsonException`, `ChokaQFatalException` | Исправить code или payload перед resurrection |
| Retries exhausted | Timeout, временная downstream failure, HTTP failure | Проверить восстановление dependency, затем bulk resurrect, если безопасно |
| Zombie | Worker crash, host eviction, heartbeat loss | Проверить idempotency и downstream state перед resurrection |
| Unknown | Неожиданная runtime/storage failure | Изучить logs и stack trace перед действиями с заданием |

## Видимость в The Deck

The Deck должен делать DLQ work понятной и повторяемой:

- queue и job type;
- failure reason;
- attempt count;
- last worker id;
- failed timestamp;
- payload и tags;
- exception summary и stack trace;
- безопасные действия: inspect, edit, resurrect, purge.

Первый вопрос оператора: "почему это задание здесь?" Второй вопрос: "безопасно ли запускать его снова?"

## Граница resurrection

Resurrection перемещает строку DLQ обратно в `JobsHot` как Pending work. Это осознанная граница: ChokaQ не выполняет строки напрямую из DLQ.

Так сохраняется один execution path:

1. Hot row выбирается fetch'ем.
2. Воркер начинает processing.
3. Heartbeat отслеживает liveness.
4. Handler выполняется через middleware.
5. Result уходит в Archive, retry или DLQ.

Если бы resurrection обходил Hot, для каждого lifecycle rule понадобилась бы вторая версия.

## Production tuning

Следите за DLQ по rate, age и reason:

| Signal | Meaning |
| --- | --- |
| DLQ rate rising | Новый deployment, dependency outage или плохой input stream |
| Old DLQ rows | Operators не закрывают failed work |
| One job type dominates | Handler-specific bug или downstream contract break |
| Zombie reason dominates | Проблема worker stability, heartbeat или host shutdown |
| Fatal reason dominates | Проблема payload validation или code correctness |

Рост DLQ обычно не лечится добавлением воркеров. Большее число воркеров может быстрее обработать плохие задания, но не может заставить плохие payloads или broken handlers успешно завершиться.

## Архитектурное решение

ChokaQ рассматривает DLQ как first-class lifecycle state, потому что failed background work требует больше, чем строка в log. Database row дает операторам достаточно контекста для investigation, сохраняет payload evidence и предоставляет controlled recovery path.

Альтернатива - выбрасывать failed work после logging или retry'ть бесконечно. Discarding теряет business operations. Infinite retry скрывает реальные дефекты и создает retry storms. DLQ - средний путь: остановить automatic execution, сохранить evidence и требовать осознанного следующего действия.

Trade-off - операционная ответственность. DLQ помогает только если команда его просматривает и имеет ясное ownership для resurrection, purge или bug fixes.

## Дополнительные вопросы

**Почему DLQ отделен от Archive?**  
Archive - retained completion history. DLQ - unresolved failed work, который может потребовать correction и resurrection. Смешивание этих read models ослабляет оба.

**Почему не resurrect'ить max-retry jobs автоматически позже?**  
Потому что исчерпание retries означает, что нормальная recovery policy уже не сработала. Automatic resurrection без новых evidence может запустить тот же failure loop.

**Что делает DLQ безопасным для enterprise operations?**  
Ясные failure reasons, неизменяемый context кроме явного edit, audited operator actions и resurrection path, который возвращает задания в нормальный Hot lifecycle.

> Дальше: [Retry And DLQ](/ru/2-lifecycle/retry-and-dlq) описывает retry path, а [Edit + Resurrect](/ru/4-the-deck/resurrect-dlq) - operator recovery.
