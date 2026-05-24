# Почему SQL Server?

## Осознанный выбор

ChokaQ использует SQL Server, потому что он дает database-level primitives,
которые совпадают с целью продукта: durable job state, transactional lifecycle
transitions, operator inspection и concurrent worker coordination внутри одной
storage boundary.

## Четыре ключевые возможности

### 1. Row-level locking: `UPDLOCK + READPAST`

Основа competing consumer pattern:

```sql
SELECT TOP (@Limit) h.*
FROM [chokaq].[JobsHot] h WITH (UPDLOCK, READPAST)
WHERE h.[Status] = 0
ORDER BY h.[Priority] DESC, ISNULL(h.[ScheduledAtUtc], h.[CreatedAtUtc]) ASC
```

| Hint | Что делает |
|------|-------------|
| `UPDLOCK` | Берет update lock на каждую выбранную row: другая transaction не может забрать ее |
| `READPAST` | Пропускает rows, уже locked другими workers: без blocking и ожидания |

**Результат:** много workers могут выполнять этот query одновременно, и каждый
committed fetch получает уникальный набор claimed rows. Pattern снижает
blocking и предотвращает duplicate claims на fetch path; SQL Server все равно
может detect и resolve deadlocks в unrelated transactions, поэтому ChokaQ
считает deadlocks transient SQL failures.

::: warning Design Boundary
Key-value и list-based queues отлично подходят для простых enqueue/dequeue
workflows. ChokaQ нужны priority ordering, delayed execution, queue filtering,
worker ownership и lifecycle inspection в одной durable model, поэтому первый
provider использует SQL Server, а не list-oriented backend.
:::

### 2. OUTPUT clause: atomic cross-table moves

`OUTPUT` clause в SQL Server позволяет ChokaQ захватывать rows во время delete
или update. ChokaQ оборачивает cross-table lifecycle moves в короткие
transactions, чтобы state transition и counters commit вместе:

```sql
DELETE FROM [chokaq].[JobsHot]
OUTPUT DELETED.* INTO [chokaq].[JobsArchive](...)
WHERE [Id] = @JobId;
```

Без distributed transaction, без two-phase commit и без application-side
"copy then delete" gap. После commit row находится либо в Hot, либо в Archive.

### 3. PAGE Compression

Archive и DLQ tables используют `DATA_COMPRESSION = PAGE`:

```sql
CONSTRAINT [PK_JobsArchive] PRIMARY KEY CLUSTERED ([Id] ASC)
WITH (DATA_COMPRESSION = PAGE)
```

PAGE compression использует:

- **Column-prefix compression**: common prefixes хранятся один раз;
- **Dictionary compression**: repeated values заменяются tokens.

Для text-heavy job payloads (JSON) это дает **50-70% storage reduction** с
минимальным CPU overhead на reads.

### 4. Filtered indexes

Fetch index покрывает только `Status = 0` (`Pending`):

```sql
CREATE NONCLUSTERED INDEX [IX_JobsHot_Fetch]
ON [chokaq].[JobsHot] ([Queue], [Priority] DESC, [ScheduledAtUtc], [CreatedAtUtc])
INCLUDE ([Id], [Type])
WHERE [Status] = 0
```

**Почему это важно:**

- если 10000 jobs processing и 50 pending, index содержит **50 entries**, а не 10050;
- index maintenance cost пропорционален pending count, а не total row count;
- B-tree остается shallow, поэтому lookup time стабильнее;
- `CreatedAtUtc` совпадает с fetch tie-breaker и избегает лишних sort/lookups на hottest path.

## Backend trade-offs

| Backend | Trade-off для текущего provider ChokaQ |
|----------|----------------|
| **PostgreSQL** | Похожий competing-consumer behavior возможен через `SKIP LOCKED`, но нужен отдельный provider и test matrix. |
| **MySQL** | Provider support потребует другой indexing и locking design. |
| **SQLite** | Полезен для embedded scenarios, но single-writer model не совпадает с concurrent worker target ChokaQ. |
| **MongoDB** | Document provider потребует отдельный lifecycle и transaction design. |
| **Redis** | Сильный вариант для fast queue primitives, но первый provider ChokaQ приоритизирует relational inspection и transactional state moves. |

::: tip PostgreSQL Support?
`SKIP LOCKED` в PostgreSQL решает похожую competing-consumer problem. Future
PostgreSQL provider архитектурно возможен, потому что `IJobStorage` abstraction
обращена к database boundary. Он должен стать отдельным package только тогда,
когда это будет реальная production support, а не пока SQL Server остается
единственным provider.
:::

## Transaction advantage

SQL Server transactions дают guarantees, которые application-level locking не
может дать:

1. **Crash safety:** если process crashes mid-operation, uncommitted changes автоматически rolled back.
2. **Isolation:** `UPDLOCK` переживает connection pooling и async context switches.
3. **Durability:** после commit data переживает power failures через WAL + checkpoints.
4. **Deadlock detection:** SQL Server lock manager автоматически detect и resolve deadlocks, хотя `READPAST` предотвращает большинство.

Это storage contract, вокруг которого ChokaQ оптимизируется. Другие backends
могут быть valid choices, но каждый из них потребует equivalent
provider-specific answer для claiming work, recording final state и recovery
после process failure.

## Read consistency policy

ChokaQ не рассматривает `NOLOCK` как общий performance trick. Dirty reads
допустимы только для passive dashboard telemetry, где UI уже является
approximate snapshot:

- summary counters;
- queue saturation health;
- recent throughput и failure-rate windows;
- top DLQ error groups.

Correctness и operator-decision paths используют committed reads или explicit
locking:

- fetch и bulkhead capacity decisions;
- worker ownership и state transitions;
- DLQ bulk previews;
- job inspectors и history pages;
- queue management rows.

Это разделение намеренное. Dashboards должны observe system, не становясь
workload. Но все, что может вызвать execution, purge, requeue, edit, pause или
capacity admission, не должно опираться на uncommitted data.

## Performance baseline tests

ChokaQ держит SQL performance честным через integration tests против реального
SQL Server container. Эти tests - guardrails, а не microbenchmarks:

- `FetchNextBatchAsync` измеряется на mixed Hot table с pending, fetched и processing rows.
- `GetSystemHealthAsync` измеряется на dashboard-sized operational snapshot по Hot, Archive и DLQ.
- Archive и DLQ history paging измеряются с committed reads и тысячами rows.

Budgets намеренно generous, потому что Docker и CI runners шумные. Tests должны
ловить query-shape regressions, missing-index mistakes и accidental unbounded
scans, а не заявлять точную latency для каждой production database. Real
deployments все равно должны использовать Query Store, wait-stat и index-usage
telemetry, но в repository теперь есть repeatable floor, который защищает самые
важные SQL paths.

<br>

> Дальше: как ChokaQ держит infrastructure dependencies маленькими и явными в [Minimal Dependency Philosophy](/ru/1-architecture/minimal-dependencies).
