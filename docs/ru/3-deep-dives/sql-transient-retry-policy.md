# SQL Transient Retry Policy

![SQL transient retry policy](/diagrams/49-sql-transient-retry-policy.png)

SQL transient retry policy обрабатывает кратковременные SQL failures: deadlocks, timeouts, service busy responses и временную недоступность database.

Эта policy относится к собственным storage operations ChokaQ. Это не то же самое, что job handler retry.

## Где это находится

`SqlRetryPolicy` оборачивает calls внутри `SqlJobStorage`.

Storage options:

| Setting | Purpose |
|---|---|
| `MaxTransientRetries` | Maximum retry attempts для transient SQL exceptions. |
| `TransientRetryBaseDelayMs` | Base delay для exponential backoff. |
| `CommandTimeoutSeconds` | Timeout для individual SQL commands. |

## Transient errors

Policy считает retryable:

- `SqlException.IsTransient`;
- `1205` deadlock;
- `-2` timeout;
- `4060` cannot open database;
- `40197` Azure SQL request processing error;
- `40501` service busy;
- `40613` database unavailable.

## Backoff and jitter

Delay растет экспоненциально:

```text
baseDelayMs * 2^(attempt - 1)
```

Jitter добавляет случайный разброс, чтобы несколько workers не retry'лись lockstep после общего SQL blip.

## Отличие от job retry

| Retry type | Retries what? | Visible as job attempt? |
|---|---|---|
| SQL transient retry | ChokaQ storage operation. | No |
| Job retry | User handler execution. | Yes |

Transient SQL retry не должен сжигать job attempt, если job фактически не дошел до user-code execution.

## Архитектурное решение

### Почему этот pattern?

ChokaQ нужен узкий SQL retry shape, и он не тащит general-purpose resilience dependency ради одной storage concern.

### Trade-offs

Local policy меньше и явнее, но менее feature-rich, чем general-purpose resilience library. Если future provider behavior станет сложнее, здесь может понадобиться pluggable policy.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| No SQL retry | Простая failure visibility. | Слишком хрупко при deadlocks/network blips. |
| General-purpose resilience dependency | Mature resilience features. | Extra dependency ради одного узкого path. |
| Retry every SQL error | Более aggressive recovery. | Может скрыть real bugs и перегрузить SQL. |

### Дополнительные вопросы

**Почему SQL retry не считается job retry?**  
Потому что user code не запускался. Job attempts отслеживают handler execution, а не storage plumbing retries.

**Зачем jitter?**  
Чтобы workers не retry'лись одновременно после shared outage.

**Какие errors не нужно retry'ть?**  
Schema errors, invalid SQL, permission failures и data contract violations.
