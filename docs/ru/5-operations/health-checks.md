# Health Checks

![Health checks](/diagrams/52-health-checks.png)

Health checks ChokaQ отвечают, usable ли host и durable queue boundary с точки зрения infrastructure.

Они не заменяют metrics. Health checks - point-in-time status; metrics показывают trends.

## Health surfaces

| Check | Purpose |
|---|---|
| SQL connectivity | Может ли host открыть SQL и увидеть нужные ChokaQ tables. |
| Worker heartbeat | Заходил ли local worker loop в работу и продолжает ли cycling. |
| Queue saturation | Ждет ли eligible work дольше configured budget. |

## SQL Connectivity

SQL health check проверяет:

- connection open succeeds;
- configured schema exists;
- required tables exist: `JobsHot`, `JobsArchive`, `JobsDLQ`, `StatsSummary`, `MetricBuckets` и `Queues`;
- command timeout policy applied.

Одна connectivity не доказывает runtime readiness. Database может быть reachable, но без runtime schema.

## Worker Heartbeat

Worker health check process-local. Он reports:

- active worker count;
- total worker capacity;
- running state;
- last worker heartbeat;
- heartbeat age.

Так ловится host, у которого SQL connection нормальный, но background service фактически не running.

## Queue Saturation

Queue saturation использует lag, а не только depth. Он смотрит на eligible pending work и reports degraded/unhealthy, когда worst queue превышает configured thresholds.

Depth спрашивает: "Сколько jobs существует?"  
Lag спрашивает: "Как долго actionable work ждет?"

## Архитектурное решение

### Почему этот pattern?

Health background jobs имеет несколько dimensions. Один check "SQL reachable" не доказывает, что workers alive или system keeps up.

### Trade-offs

Queue lag health может пометить host degraded, даже если process technically alive. Это намеренно: queue processor, который не успевает, является operational problem.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Liveness-only endpoint | Просто. | Пропускает broken workers и saturated queues. |
| SQL-only endpoint | Validates storage. | Пропускает dead background loops. |
| Metrics-only alerting | Отлично для trends. | Не идеально для readiness probes. |

### Дополнительные вопросы

**Почему queue lag вместо depth?**  
Depth не содержит job duration context. Lag измеряет user-visible waiting time для eligible work.

**Может ли health быть green, пока DLQ растет?**  
Да. DLQ growth обычно alert/metric problem, а не обязательно readiness failure.

**Health endpoints должны быть public?**  
Нет. Они должны быть infrastructure-scoped или protected.
