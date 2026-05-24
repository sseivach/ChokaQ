# Failure Taxonomy

![Failure taxonomy](/diagrams/59-failure-taxonomy.png)

Failure taxonomy превращает raw exceptions в операционный смысл. ChokaQ сохраняет `FailureReason`, когда задание попадает в DLQ, чтобы operators могли triage'ить по category, а не сначала читать каждый stack trace.

## Почему taxonomy важна

Два failed jobs могут требовать совершенно разных действий:

- transient HTTP timeout может быть безопасен для retry;
- malformed payload нужно исправить перед retry;
- zombie job мог уже отправить email или списать карту;
- admin-cancelled job мог быть остановлен намеренно.

Taxonomy сохраняет это различие в данных.

## Common reasons

| Reason | Meaning | Typical response |
|---|---|---|
| `MaxRetriesExceeded` | Retry budget исчерпан. | Исправить cause, затем sample resurrection. |
| `FatalError` | Classified as non-transient. | Сначала исправить code или payload. |
| `Timeout` | Handler превысил timeout. | Настроить handler или timeout; проверить side effects. |
| `Cancelled` | Operator/runtime cancellation. | Подтвердить intention. |
| `Zombie` | Heartbeat истек во время processing. | Проверить side effects перед retry. |
| `CircuitBreakerOpen` | Execution заблокирован открытым circuit. | Исправить downstream перед forcing recovery. |
| `Throttled` | Downstream overload/rate limit. | Уважать retry-after и снизить pressure. |
| `Transient` | Retryable family eventually exhausted. | Проверить downstream instability. |
| `HeartbeatFailure` | Heartbeat write failure пересек policy. | Проверить SQL/storage pressure. |
| `RetryLifetimeExpired` | Job стал слишком старым для retry. | Рассматривать как stale work. |

## Связь со Smart Worker

Smart Worker classification решает, должна ли ошибка retry'ться или fast-fail'ить в DLQ. Failure taxonomy - это сохраненный operator-facing result.

## Архитектурное решение

### Почему этот pattern?

Operators нужна routing metadata. Raw exception text полезен для debugging, но safe bulk actions, dashboards и incident triage также требуют stable failure categories.

### Trade-offs

Classification может быть неверной, если application exceptions слишком generic. Apps должны выбрасывать meaningful fatal/throttled/transient signals, когда это возможно.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Store stack traces only | Просто. | Плохая фильтрация и небезопасные bulk decisions. |
| Use exception type only | Автоматически. | Теряются operational categories вроде zombie/cancelled. |
| App-owned categories only | Гибко. | Нет consistent runtime semantics. |

### Дополнительные вопросы

**Почему failure reason сохраняется отдельно от error details?**  
Потому что operators нужны stable categories для filtering, dashboards и actions.

**Какие failures требуют inspection перед retry?**  
Zombie, fatal payload/code errors, cancelled jobs и stale jobs за пределами lifetime policy.

**Как это помогает The Deck?**  
The Deck может group, filter, badge и bulk-preview на основе reason, а не free-form exception text.
