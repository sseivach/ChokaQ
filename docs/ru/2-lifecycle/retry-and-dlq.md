# Retry And DLQ Lifecycle

![Retry and DLQ lifecycle](/diagrams/12-retry-dlq-lifecycle.png)

Retries и DLQ - это разные решения.

Retry означает: "Это задание может успешно выполниться позже."  
DLQ означает: "Автоматическое выполнение нужно остановить; задание должен проверить человек или repair workflow."

ChokaQ оставляет это различие явным, чтобы transient outages не превращались в операторский шум, а permanent failures не сжигали worker capacity бесконечно.

## Нормальный retry path

1. Воркер переводит задание из `Fetched` в `Processing`.
2. Handler выбрасывает exception.
3. Processor классифицирует ошибку.
4. Если ошибка retryable и retry budget еще не исчерпан, ChokaQ вычисляет будущий `ScheduledAtUtc`.
5. Строка остается в `JobsHot`.
6. Воркеры пропускают строку, пока не наступит время запуска.
7. Следующее выполнение увеличивает `AttemptCount`, когда задание переходит в `Processing`.

Retry scheduling хранится в SQL. Delay не принадлежит никакому спящему thread'у.

## DLQ path

Задание перемещается в `JobsDLQ`, когда автоматическая обработка больше не является правильным решением. Типичные причины:

| Reason | Meaning | Operator action |
|---|---|---|
| `MaxRetriesExceeded` | Retry budget исчерпан. | Проверить ошибку, исправить dependency или payload, затем resurrect, если безопасно. |
| `FatalError` | Ошибка классифицирована как non-transient. | Исправить code/data перед повторным запуском. |
| `Cancelled` | Operator или runtime path отменил задание. | Подтвердить, что отмена была намеренной. |
| `Zombie` | Heartbeat выполнения истек. | Проверить side effects перед resurrection. |
| `CircuitBreakerOpen` | Runtime отказал в выполнении, потому что circuit открыт. | Исправить downstream или дождаться восстановления. |
| `Throttled` | Downstream попросил систему замедлиться. | Учитывать retry-after или настроить traffic. |
| `RetryLifetimeExpired` | Возраст задания превысил policy. | Рассматривать как stale work. |

DLQ - это не просто таблица ошибок. Это операционная очередь для работы, которая требует инженерного решения.

## Входные данные для retry delay

Retry delay использует policy из `ChokaQ:Retry`:

| Setting | Purpose |
|---|---|
| `MaxAttempts` | Ограничивает, сколько выполнений может стартовать. |
| `BaseDelay` | Начальная задержка после transient failure. |
| `BackoffMultiplier` | Коэффициент экспоненциального роста. |
| `MaxDelay` | Жесткий верхний предел retry delay. |
| `JitterMaxDelay` | Случайный разброс, чтобы избежать синхронных retry storms. |
| `CircuitBreakerDelay` | Delay, когда выполнение заблокировано открытым breaker'ом. |
| `MaxJobAge` | Не дает очень старым заданиям retry'ться бесконечно. |

## Зачем нужен DLQ

Без DLQ у queue engine остаются два слабых варианта:

- бесконечно retry'ть плохую работу;
- тихо выбрасывать плохую работу.

ChokaQ выбирает третий вариант: сохранить failed job вместе с payload, failure reason, error details, attempt count, queue, type key и timestamps. После этого оператор может inspect, edit, resurrect, purge или оставить строку для audit.

## Архитектурное решение

### Почему этот pattern?

At-least-once системам нужен durable terminal state для работы, которую automatic execution не может безопасно завершить. DLQ дает такой terminal state без уничтожения evidence.

### Trade-offs

DLQ создает операционную ответственность. Кто-то должен следить за ним и принимать решения. Выигрыш - явные evidence и operator control вместо бесконечного retry churn вокруг одного poison message.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Infinite retries | Просто реализовать. | Retry storms, лишняя нагрузка на воркеры, скрытые poison jobs. |
| Drop failed jobs | Очереди остаются чистыми. | Теряется принятая работа и уничтожаются audit evidence. |
| External error topic only | Хорошо работает в broker systems. | Все равно нужны storage, tooling и operator workflow. |

### Когда не использовать этот подход

Для одноразовых telemetry events, где потеря допустима, durable DLQ может быть слишком тяжелым. Для business workflows, payments, emails, reports и integrations DLQ безопаснее как default.

### Дополнительные вопросы

**Почему не retry forever?**  
Потому что permanent failures превращаются в retry storms и потребляют capacity, нужную здоровым заданиям.

**Почему zombies идут в DLQ, а не в retry?**  
Потому что handler мог уже произвести side effects. Blind retry может продублировать emails, charges или external writes.

**Что делает retry безопасным?**  
Только idempotency handler'а и downstream idempotency делают external side effects безопасными. ChokaQ может сохранить и перепланировать работу, но не может сделать произвольные side effects exactly-once.
