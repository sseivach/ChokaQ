# Почему ChokaQ

## Проблема

ChokaQ спроектирован для команд, которым нужна durable background processing
без превращения job system в отдельную инфраструктурную платформу. На практике
.NET-команде часто приходится выбирать, сколько persistence, observability и
operator control она готова поддерживать сама:

1. `Channel<T>` + `BackgroundService` - маленький и прямой вариант, но он
   process-local. Для durability, monitoring, retries и recovery потребуется
   дополнительная работа.
2. Более крупная job platform может дать больше встроенных возможностей, но
   приносит больше инфраструктуры, зависимостей и configuration surface.

ChokaQ занимает середину: **надежность на базе SQL Server при простоте
in-process runtime**.

## Что дает ChokaQ

| Возможность | ChokaQ |
|---------|--------|
| **Storage** | SQL Server через raw ADO.NET: атомарно и транзакционно |
| **Dependency Footprint** | Core использует Microsoft abstractions; SQL использует официальный `Microsoft.Data.SqlClient`; без общего ORM, mapper или resilience dependency |
| **Dashboard** | **The Deck** на Blazor Server + SignalR, встроен в пакет |
| **ORM** | **Custom SqlMapper**: 172 строки, без зависимостей |
| **DLQ Management** | **Edit + Resurrect**: исправление payloads в браузере |
| **Concurrency Control** | **DynamicConcurrencyLimiter**: динамическое runtime scaling |
| **Error Classification** | **Smart Worker**: маршрутизация fatal и transient errors |
| **Bulkhead** | Per-queue concurrency limits на уровне базы данных |
| **Circuit Breaker** | Встроенный per job type, без внешних библиотек |
| **Zombie Detection** | **ZombieRescueService**: автоматический heartbeat monitoring |
| **Handler Invocation** | **Expression Trees**: кэшированные compiled delegates |
| **Table Design** | **Three Pillars**: физическое разделение Hot/Archive/DLQ |

## Проблема смешанного lifecycle-хранилища

Lifecycle store может держать pending, succeeded и failed jobs в одной
физической таблице. Такой подход легко начать, но active fetch и historical
retention тянут таблицу в разные стороны. Со временем:

- active indexes вынуждены сосуществовать с долгоживущими historical rows;
- fetch queries тратят больше работы на фильтрацию строк, которые уже не могут
  выполняться;
- cleanup и retention начинают влиять на hot-path performance management.

Архитектура **Three Pillars** в ChokaQ решает это физическим разделением данных:

| Pillar | Содержит | Оптимизирован для |
|--------|----------|---------------|
| **JobsHot** | Только Pending/Fetched/Processing | High-concurrency OLTP, `UPDLOCK + READPAST` |
| **JobsArchive** | Только Succeeded | Read-heavy analytics, PAGE compression |
| **JobsDLQ** | Только Failed/Cancelled/Zombie | Manual review, resurrection |

Hot table остается сфокусированной на активной работе. Fetch queries остаются
предсказуемее, чем в схемах, где pending и historical rows смешаны в одной
таблице.

## Философия минимальных зависимостей

ChokaQ намеренно держит зависимости маленькими:

```text
ChokaQ.Core
├── Microsoft.Extensions.Hosting.Abstractions
├── Microsoft.Extensions.DependencyInjection.Abstractions
└── Microsoft.Extensions.Logging.Abstractions

ChokaQ.Storage.SqlServer
└── Microsoft.Data.SqlClient (official Microsoft ADO.NET driver)
```

**Что ChokaQ оставляет внутри границы проекта:**

| Область | Реализация ChokaQ | Почему |
|---------------|------------|-----|
| SQL mapping | `SqlMapper` + `TypeMapper` | Полный контроль над parameter mapping и transitive dependencies |
| Storage resilience | `SqlRetryPolicy` + `InMemoryCircuitBreaker` | Policies, настроенные под ChokaQ storage и execution failures |
| SQL query shape | Raw SQL templates в `Queries.cs` | Точный контроль над locking hints, `OUTPUT` clauses и CTEs |
| Handler dispatch | `BusJobDispatcher` + Expression Trees | Cached compiled delegates для typed handler invocation |

::: warning Design Decision
Даже небольшие зависимости становятся частью compatibility surface host
application. ChokaQ держит SQL mapping layer узким, чтобы пакет контролировал
query behavior и избегал лишнего transitive version pressure.
:::

## Production patterns, уже реализованные в runtime

### 1. Atomic State Transitions

Перемещения между pillars используют transaction-scoped SQL state transitions с
ownership guards. Без distributed transactions. Без two-phase commit. Если
transition не применен, воркер видит этот результат и не публикует ложное
success notification.

### 2. Self-Healing через `ZombieRescueService`

`BackgroundService` запускается каждые 60 секунд:

- **Step 1:** находит jobs, застрявшие в `Fetched` state: воркер упал до
  processing, строка сбрасывается в `Pending`;
- **Step 2:** находит jobs, застрявшие в `Processing` с expired heartbeat:
  строка архивируется в DLQ как `Zombie`.

### 3. Smart Error Handling

Smart Worker различает:

- **Fatal errors** (`NullReferenceException`, `ArgumentException`,
  `JsonException`): сразу в DLQ, без retries;
- **Transient errors**: timeout, краткий сетевой сбой и похожие случаи:
  exponential backoff with jitter.

### 4. Observable by Default

Native OpenTelemetry через `System.Diagnostics.Metrics`:

```text
chokaq.jobs.enqueued    (Counter)
chokaq.jobs.completed   (Counter)
chokaq.jobs.failed      (Counter)
chokaq.jobs.processing_duration (Histogram)
chokaq.jobs.queue_lag   (Histogram)
chokaq.jobs.dlq         (Counter)
chokaq.jobs.retried     (Counter)
chokaq.workers.active   (UpDownCounter)
```

ChokaQ-specific exporter не нужен. Hosts слушают meter `"ChokaQ"` через обычную
OpenTelemetry-настройку. Metric tag cardinality ограничивается
`ChokaQ:Metrics`, поэтому динамические queue names, job types, errors или
failure reasons после заданного бюджета схлопываются в `other`.

Lifecycle logs также используют стабильные `EventId`. Операторы могут напрямую
искать `JobRetriesExhaustedDlq` или `ZombieJobsArchived`, а не парсить текст
log messages.

## Когда выбирать ChokaQ

ChokaQ хорошо подходит, когда приложение уже использует SQL Server, а команда
хочет durable background work с небольшим dependency footprint:

- SQL Server-centric environments;
- durable queue state без отдельного broker для background work;
- real-time dashboard с operator recovery workflows;
- database-level queue isolation и bulkhead controls;
- окружения, где важна небольшая transitive dependency surface;
- команды, которые хотят явно видеть SQL query behavior и lifecycle transitions.

**Не проектировался для:**

- event streaming systems, где replayable logs и очень высокий event volume -
  основное требование;
- cross-service pub/sub messaging patterns;
- PostgreSQL или Redis storage в текущей preview line;
- recurring/scheduled jobs, которые пока не поддерживаются;
- multi-region distributed coordination.

## Архитектурные заметки

Документация идет дальше setup snippets, потому что background processing
systems часто падают так, что это трудно разбирать постфактум. Design pages
объясняют operational patterns, на которые опирается ChokaQ:

- at-least-once processing и idempotency;
- SQL competing consumers;
- worker ownership и lease checks;
- backpressure и bounded buffers;
- bulkhead isolation;
- circuit breakers;
- retry с exponential backoff и jitter;
- DLQ taxonomy и repair workflows;
- observability, health checks и metric cardinality;
- secure administrative control planes.

<br>

> Убедились? Переходите к [Быстрому старту](/ru/getting-started) или изучайте [Three Pillars Architecture](/ru/1-architecture/three-pillars).
