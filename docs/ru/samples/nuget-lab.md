# Local NuGet Lab

`samples/ChokaQ.Sample.NuGetLab` - package-consumer smoke application для ChokaQ. Она существует ради одной конкретной причины: доказать, что обычное host application может установить top-level package `ChokaQ` из NuGet feed и использовать product без source `ProjectReference` shortcuts.

Откройте `samples/ChokaQ.Sample.NuGetLab/ChokaQ.Sample.NuGetLab.sln` в IDE. Solution намеренно отделен от root `ChokaQ.sln`: root solution validates source development, а NuGetLab validates packaged consumer experience.

Lab - не benchmark и не production template. Это high-signal validation app, который проверяет features, важные new user перед доверием background job engine.

## What this sample proves

Lab validates NuGet consumer experience:

- app references only `ChokaQ` as its top-level package;
- package `ChokaQ` transitively brings in `ChokaQ.Abstractions`, `ChokaQ.Core`, `ChokaQ.Storage.SqlServer` и `ChokaQ.TheDeck`;
- app starts against SQL Server storage;
- ChokaQ can provision its schema on startup;
- jobs can be enqueued through `IChokaQQueue`;
- typed Bus jobs dispatched through `ChokaQJobProfile`;
- handlers run through dependency injection and middleware;
- The Deck can connect to running app;
- health checks report SQL, worker и queue-saturation status;
- idempotency keys collapse duplicate active work;
- delayed jobs become eligible later;
- retry, throttling, timeout и fatal-DLQ paths visible;
- queue pause/resume и worker scaling work through public APIs.

Если это app не restore'ится или не runs from NuGet feed, package-consumer path
не здоров.

## Why it uses SQL Server

Durable path preview - SQL Server mode. In-memory mode полезен для demos, tests и volatile local experiments, но он process-local и не переживает restart. NuGet smoke app, который тестирует только in-memory execution, пропустил бы самые важные части ChokaQ:

- persisted `JobsHot` admission;
- SQL schema creation;
- worker ownership;
- atomic Hot-to-Archive и Hot-to-DLQ transitions;
- queue pause/resume в database;
- dashboard history и DLQ views;
- health checks, зависящие от storage.

Поэтому NuGetLab намеренно стартует с SQL.

## Run the lab from NuGet

Start SQL Server. Repository compose file запускает SQL container named `chokaq-sql`.

```powershell
docker compose up -d sqlserver
```

Run the lab.

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

Launch profile starts app in Development mode. В Development mode lab может discover running Docker container named `chokaq-sql`, прочитать published SQL port и `MSSQL_SA_PASSWORD`, затем автоматически построить local connection string.

Если нужно использовать другой SQL Server instance, передайте explicit connection string:

```powershell
$env:CHOKAQ_NUGET_LAB_SQL="Server=localhost,1433;Database=ChokaQNuGetLab;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;"
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

Если configured database не существует, sample создает ее через `master`, затем дает ChokaQ create/update собственную schema. SQL login нужен database creation permission для first run.

## What to open

| URL | Purpose |
|---|---|
| `http://localhost:5317/` | Lab launcher with scenario buttons and live snapshot. |
| `http://localhost:5317/chokaq` | The Deck dashboard for jobs, queues, DLQ, history, health, and circuits. |
| `http://localhost:5317/health` | ASP.NET Core health endpoint. |
| `http://localhost:5317/api/lab/snapshot` | JSON snapshot of workers, queues, summary stats, and system health. |

## Scenarios

### Mixed Load

Endpoint:

```text
POST /api/lab/scenarios/mix?count=25
```

Enqueues blend of work across `critical`, `reports` и `unstable` queues:

- receipt email jobs;
- report rendering jobs;
- webhook delivery jobs;
- payment capture jobs;
- intentionally slow jobs;
- throttled partner jobs;
- some delayed jobs.

Используйте scenario, чтобы подтвердить: jobs попадают в SQL, workers drain them, The Deck updates, queue-level signals make sense.

### Idempotency Probe

Endpoint:

```text
POST /api/lab/scenarios/idempotency
```

Отправляет five payment jobs с одним business idempotency key. Expected behavior - одна active logical operation в `JobsHot`. Это не означает, что ChokaQ делает payment processors exactly-once. Это означает, что ChokaQ может suppress duplicate active queue admissions для deterministic operation key.

Handlers, которые charge cards, send emails или call external APIs, все равно нуждаются в provider-side idempotency, unique constraints или application-level dedupe.

### Failure Matrix

Endpoint:

```text
POST /api/lab/scenarios/failures
```

Создает четыре different failure families:

| Job | Expected behavior |
|---|---|
| Poison payload | Moves to DLQ as fatal work. |
| Transient webhook | Retries and then either succeeds or exhausts retry policy. |
| Throttled partner | Uses retry-after semantics before retrying. |
| Slow job | Hits the configured timeout and becomes visible as timeout work. |

Используйте scenario для inspection failure taxonomy в The Deck. Не treat every DLQ row the same way. `Fatal` payload обычно требует code/data repair; `Throttled` row часто означает, что downstream dependency нужно время или reduced concurrency.

### Delayed Jobs

Endpoint:

```text
POST /api/lab/scenarios/delayed
```

Schedules jobs into future:

| Job | Queue | Delay |
|---|---|---|
| Receipt email | `critical` | About 30 seconds |
| Report render | `reports` | About 45 seconds |

Важное поведение: эти jobs accepted and stored immediately, но workers не должны run them immediately. ChokaQ пишет их в SQL `JobsHot` с future `ScheduledAtUtc`. Пока это время не наступит, SQL fetch query считает rows not eligible.

Думайте об этом как "stored now, runnable later". Это не process-local `Task.Delay` и не timer, который исчезает при app restart. Schedule persisted with job row. Если NuGetLab host restart'ится, пока job ждет, job все еще в SQL и сможет run after due.

What to observe:

1. Click `Delayed enqueue`.
2. API response says two jobs were enqueued.
3. They should not immediately become `Processing`.
4. After roughly 30 and 45 seconds, they become eligible.
5. Workers then process them through normal lifecycle: `Pending -> Fetched -> Processing -> Succeeded`.

В current preview The Deck еще не имеет dedicated `DELAYED` badge или `Scheduled For` column. Это UI improvement tracked separately. Сейчас important validation: future-due jobs persisted, do not run early, and later enter normal worker lifecycle.

## Runtime Controls

NuGetLab включает operational controls, которые вызывают те же public APIs, что и real host:

| Control | API | What it validates |
|---|---|---|
| Set workers to 1/4/8 | `POST /api/lab/workers/{count}` | Runtime worker capacity changes. |
| Pause `unstable` | `POST /api/lab/queues/unstable/pause` | Workers skip paused queues. |
| Resume `unstable` | `POST /api/lab/queues/unstable/resume` | Paused work can drain again. |
| Limit `reports` | `POST /api/lab/queues/reports/max-workers/1` | Queue-level concurrency isolation. |
| Unlimit `reports` | `POST /api/lab/queues/reports/max-workers/0` | Removes the per-queue cap. |

Эти controls intentionally simple. The Deck provides richer UI workflows, но lab держит HTTP calls visible and repeatable.

## Validate local packages

Для release engineering этот же lab можно восстановить из локальных `.nupkg`.
Сначала соберите пакеты в `artifacts/packages`:

```powershell
New-Item -ItemType Directory -Force artifacts\packages | Out-Null
dotnet build ChokaQ.sln --configuration Release --no-restore
dotnet pack src\ChokaQ.Abstractions\ChokaQ.Abstractions.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Core\ChokaQ.Core.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ\ChokaQ.csproj --configuration Release --no-build --no-restore -o artifacts\packages
```

Затем восстановите lab с explicit sources:

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source artifacts\packages --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

## Troubleshooting

### The app cannot find a connection string

Используйте один из вариантов:

- start repository SQL container with `docker compose up -d sqlserver`;
- set `CHOKAQ_NUGET_LAB_SQL`;
- set `ConnectionStrings:ChokaQDb`;
- set `ChokaQ:SqlServer:ConnectionString`.

Development-mode Docker discovery ищет только container named `chokaq-sql`.

### Restore cannot find ChokaQ

Восстановите sample из nuget.org явно:

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
```

Если вы repack same preview version during local development, очистите cached `ChokaQ*` packages или restore with fresh package cache. NuGet package caches version-based, поэтому newer local `.nupkg` with same version может игнорироваться, пока cached copy не removed.

### First startup logs missing table errors

На brand-new database hosted workers могут start, пока schema initialization running. Важен final startup state:

- `ChokaQ database initialization completed successfully`;
- `/health` returns `Healthy`;
- `/api/lab/snapshot` returns JSON.

Если missing-table errors continue after initialization, schema did not complete или app connects to different database/schema than expected.

### `/health` is unhealthy

Откройте `/health`, проверьте application logs и likely causes:

- SQL Server container still starting;
- SQL password или port wrong;
- app cannot create database;
- schema initialization failed;
- worker heartbeat stale because process overloaded;
- queue lag crossed configured unhealthy threshold.

Response guidance: [SLOs And Alerts](/ru/5-operations/slo-alerts) и [Operations Runbooks](/ru/5-operations/runbooks).
