# Heartbeat

![Heartbeat contract](/diagrams/32-heartbeat-contract.png)

Heartbeat - это механизм, с помощью которого ChokaQ отличает долго выполняющееся здоровое задание от задания, которым владеет мертвый воркер.

Есть два понятия heartbeat:

| Heartbeat | Stored where | Purpose |
|---|---|---|
| Worker heartbeat | Process memory через `IWorkerManager.LastHeartbeatUtc` | Доказывает для health checks, что локальный worker loop жив. |
| Job heartbeat | `JobsHot.HeartbeatUtc` | Доказывает, что конкретное processing job все еще принадлежит живому execution. |

## Job Heartbeat Contract

Когда задание переходит в `Processing`, ChokaQ устанавливает `StartedAtUtc` и `HeartbeatUtc`. Пока handler выполняется, heartbeat task периодически обновляет `HeartbeatUtc`.

Позже zombie rescue проверяет processing rows, чей heartbeat старше настроенного timeout. Такие строки перемещаются в DLQ как `Zombie`, чтобы оператор мог оценить риск side effects перед resurrection.

## Почему heartbeat, а не start time?

Один только start time не отличает здоровый долгий report от мертвого воркера. Heartbeat дает воркеру способ сказать: "Это задание все еще выполняется."

## Configuration

Важные настройки:

| Setting | Meaning |
|---|---|
| `Execution.HeartbeatIntervalMin` / `HeartbeatIntervalMax` | Jittered interval для per-job heartbeat writes. |
| `Execution.HeartbeatFailureThreshold` | Количество failed writes перед reporting degraded heartbeat state. |
| `Execution.CancelOnHeartbeatFailure` | Нужно ли отменять execution при heartbeat write failure. |
| `Recovery.ProcessingZombieTimeout` | Возраст, после которого missing heartbeat превращает processing work в zombie DLQ. |
| `Queues.*.ZombieTimeoutSeconds` | Optional per-queue override. |

Zombie timeout должен быть заметно больше heartbeat interval.

## Failure path

Если heartbeat writes fail, ChokaQ записывает `chokaq.jobs.heartbeat_failures`. В зависимости от policy задание может продолжить работу или быть отменено. Если процесс умирает, heartbeat останавливается, и zombie rescue в итоге перемещает задание в DLQ.

## Архитектурное решение

### Почему этот pattern?

Heartbeat - это lease freshness signal. Он дешевле и точнее, чем считать любое долгое задание мертвым после фиксированного timeout от start time.

### Trade-offs

Heartbeat writes добавляют database traffic. Интервал должен быть достаточно длинным, чтобы не создавать write noise, и достаточно коротким, чтобы обнаруживать dead workers в рамках operational budget.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Timeout только от `StartedAtUtc` | Просто. | Убивает здоровые долгие задания. |
| Только external worker registry | Хорошая видимость процесса. | Не доказывает progress конкретного задания. |
| Без heartbeat | Меньше database traffic. | Dead workers оставляют processing rows неоднозначными. |

### Дополнительные вопросы

**Почему heartbeat-expired jobs перемещаются в DLQ?**  
Потому что user code мог уже произвести side effects. Оператор должен решить, безопасен ли retry.

**Какие timeout values опасны?**  
Значения, близкие к heartbeat interval. Обычный jitter или transient SQL latency могут сделать здоровые задания похожими на dead.

**Какая метрика здесь важна?**  
`chokaq.jobs.heartbeat_failures`, плюс строки DLQ с `FailureReason = Zombie`.
