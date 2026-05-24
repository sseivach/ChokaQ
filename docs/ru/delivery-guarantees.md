# Delivery Guarantees

ChokaQ спроектирован вокруг **at-least-once execution**.

Это значит, что задание, принятое durable-хранилищем, должно либо завершиться,
либо уйти в retry, либо стать видимым для операторского действия в DLQ. Это не
значит, что внешние side effects выполняются exactly-once.

Storage-level граница описана в [Transaction Integrity](/ru/3-deep-dives/transaction-integrity).  
Защита от duplicate execution на уровне handler описана в [Idempotency Middleware](/ru/3-deep-dives/idempotency-middleware).

## Что гарантирует ChokaQ

- SQL Server mode сохраняет принятую работу в `JobsHot` и использует
  ownership-aware state transitions при переносе jobs в Archive, DLQ или
  delayed retry.
- Stale worker, который потерял ownership, не должен иметь возможность
  финализировать job, уже принадлежащий другому worker.
- `Fetched` jobs, которые не начали выполнять пользовательский код, recovery
  может вернуть в `Pending`.
- `Processing` jobs с expired heartbeat переносятся в DLQ как `Zombie` jobs для
  operator review.
- Retry attempts используют настроенные limits и delayed scheduling.
- The Deck и storage APIs показывают DLQ state, чтобы failed, cancelled,
  timed-out или zombie jobs можно было осознанно разобрать и обработать.

## Чего ChokaQ не гарантирует

- ChokaQ не гарантирует exactly-once side effects.
- ChokaQ не знает, успел ли handler отправить email, списать деньги, записать
  данные в другую базу или вызвать third-party API перед crash или timeout.
- ChokaQ не делает automatic retry для `Processing` zombies, потому что повтор
  уже начатого job может продублировать внешние side effects.
- ChokaQ не обещает strict FIFO ordering, если конкретная queue policy и тесты
  явно не фиксируют такое поведение.
- In-memory mode не durable. Принятая in-memory work является process-local и
  теряется при завершении процесса.

## Delayed Execution

Delayed job - это работа, которую ChokaQ уже принял, сохранил и обязался
помнить, но которую workers пока не имеют права выполнять.

Это отличается от `Task.Delay(...)` внутри приложения. При `Task.Delay` ожидание
обычно живет в одном процессе. Если процесс завершается, timer исчезает вместе
с ним. В SQL Server mode ChokaQ сохраняет job в `JobsHot` с будущим значением
`ScheduledAtUtc`. Расписание - это данные, а не in-memory timer.

Durable flow:

1. Приложение вызывает `EnqueueAsync(..., delay: someTimeSpan)`.
2. ChokaQ сразу записывает job в SQL Server.
3. Строка остается в `JobsHot`, а `ScheduledAtUtc` указывает будущий UTC time.
4. Workers пропускают эту строку, пока `ScheduledAtUtc` остается в будущем.
5. Когда scheduled time наступает, обычный worker fetch path может забрать job
   и обработать его.

Ключевой момент: delayed job уже существует до того, как становится eligible.
Если host перезапускается во время ожидания, job все еще находится в SQL. Если
один worker умирает до scheduled time, другой worker сможет забрать job позже.

Delayed execution использует ту же базовую идею, что и retries. Transient
failure не требует sleeping thread. ChokaQ оставляет job в `JobsHot`,
вычисляет следующий due time, сохраняет это время и дает workers игнорировать
job, пока оно не станет due.

Queue lag намеренно игнорирует future-due work, пока она не становится
eligible. Job, запланированный на 30 секунд вперед, не должен делать очередь
unhealthy только потому, что он уже хранится 29 секунд. Когда job становится
due, lag считается от due time, потому что именно тогда job стал actionable.

## Checklist идемпотентности handler

Любой handler, который выполняет external side effects, нужно считать
идемпотентной работой.

Используйте один или несколько паттернов:

- стабильный business idempotency key: order ID, payment attempt ID, invoice ID,
  webhook event ID или email campaign recipient ID;
- unique constraint в системе, которая владеет side effect;
- outbox table, когда local database update и последующую external publish
  нужно координировать;
- provider-supported idempotency key для платежей, email, webhooks или API
  calls;
- flow `read before write` или `upsert`, где повтор handler сходится к тому же
  финальному состоянию;
- clear compensation или manual review для side effects, которые нельзя сделать
  идемпотентными.

Idempotency TTL - это бизнес-решение. Payment key может требовать более долгого
retention, чем notification key. Выбирайте TTL по тому, как долго реально могут
приходить дубликаты и насколько дорого хранить marker.

Optional idempotency middleware является claim-based для stores, которые
реализуют `IIdempotencyClaimStore`: оно записывает `InProgress` claim до
запуска handler, пропускает concurrent duplicates и записывает completion marker
только после успешного выполнения handler. Это снижает duplicate handler
execution для одного active key, но все равно не делает внешние системы
exactly-once.

## Timeout, Cancellation и Shutdown

`Execution.DefaultTimeout` - это граница выполнения handler. Если handler
превышает эту границу, ChokaQ отменяет handler token и финализирует job через
timeout path.

Admin cancellation имеет другой intent: оператор попросил ChokaQ остановить или
удалить работу. Public docs и operator views должны показывать timeout и admin
cancellation как разные outcomes.

Shutdown является cooperative. Workers могут release `Fetched` jobs, которые не
начали пользовательский код. Начатые `Processing` jobs не возвращаются
автоматически в `Pending`, потому что handler уже мог произвести side effects.
Если процесс умирает до finalization, recovery переносит expired `Processing`
row в DLQ как zombie.

Shutdown budgets должны быть согласованы изнутри наружу:

- `Worker.ShutdownGracePeriod` ограничивает shutdown in-memory worker loop.
- `SqlServer.WorkerShutdownGracePeriod` ограничивает SQL active processing
  drain.
- `.NET HostOptions.ShutdownTimeout` должен быть длиннее ChokaQ worker budget
  плюс обычная latency finalization.
- Kubernetes `terminationGracePeriodSeconds` должен быть длиннее host shutdown
  timeout.

State outcomes во время shutdown:

- `Pending` rows не трогаются.
- `Fetched` rows, которые не начали пользовательский код, можно release обратно
  в `Pending`.
- Cooperative `Processing` handler может увидеть cancellation и быть
  rescheduled for retry.
- `Processing` handler, который завершился внутри budget, финализируется
  штатно.
- `Processing` handler, который игнорирует cancellation, может пережить ChokaQ
  shutdown budget; если host затем убьет процесс, zombie recovery позже
  перенесет его в DLQ.

## Retry Visibility и Ordering

Transient retries остаются в `JobsHot` с будущим `ScheduledAtUtc`. Workers снова
видят их только после наступления due time. Retry delay считается из
`Retry.BaseDelay`, `Retry.BackoffMultiplier`, `Retry.MaxDelay`, optional
throttling `RetryAfter` и jitter.

`Retry.MaxAttempts` ограничивает число execution starts. `Retry.MaxJobAge`
ограничивает wall-clock lifetime. Если следующий retry превысит lifetime budget,
job переносится в DLQ с `RetryLifetimeExpired`, а не планируется снова.

SQL fetch order: сначала priority, затем due scheduled time, затем creation time
внутри настроенных queue и worker-capacity constraints. Это best-effort
scheduling contract, а не strict FIFO. High-priority traffic может выполняться
раньше более старой low-priority work по design; используйте отдельные queues и
queue `MaxWorkers`, если workload требует isolation.

## Граница In-Memory Mode

In-memory mode предназначен для demos, tests, local development и volatile
workloads. Он использует process memory и `System.Threading.Channels`; это не
durable backlog. Для production work, которая должна переживать restart,
используйте SQL Server mode.

In-memory worker управляется channel. Успешный enqueue записывает Hot row, а
затем записывает job object в in-process channel. Channel item - это execution
notification; Hot row - control/audit row, который используется для отбрасывания
stale channel items. Если процесс завершается после создания Hot row, но до
drain channel item, orphaned Hot row не восстанавливается последующим process
restart. Используйте SQL Server mode, когда accepted work должна переживать
restarts.

Перед dispatch пользовательского кода in-memory worker проверяет, что Hot row
все еще `Pending`, а persisted type key и payload все еще соответствуют channel
item. Если operator cancel, DLQ move, edit, resurrection или duplicate channel
write изменили Hot row, stale channel item пропускается и не выполняется.

Admin restart из DLQ resurrects row в Hot и затем пытается записать новый item в
in-process channel. In-memory bulk restart логирует requeue results по
категориям: `requeued`, missing Hot row, not Pending, unknown type, empty
payload, failed. Process crash между resurrection и channel requeue может
оставить orphaned Hot row; это принятое preview limitation недолговечного
in-memory mode.

## Type Keys и Payload Compatibility

Для Bus jobs относитесь к profile `typeKey` как к persisted message-contract
name. Предпочитайте стабильные semantic keys, например `email.send.v1`, вместо
CLR class names.

При изменении persisted job DTO:

- additive fields обычно безопаснее, чем rename или remove fields;
- breaking schema changes должны использовать новый type key;
- изменения serializer options могут повлиять на старые rows, уже сохраненные в
  SQL или DLQ;
- держите old handlers или migration guidance, если old jobs еще могут
  существовать.

## Operational Review

Когда job попадает в DLQ, сначала изучите failure reason и только потом
resurrect. `Zombie`, `Timeout`, `Cancelled`, `FatalError`, `Transient`,
`RetryLifetimeExpired` и `MaxRetriesExceeded` означают разные вещи с
операционной точки зрения. Zombie стоит resurrect только тогда, когда handler
идемпотентен или external side effect уже проверен.
