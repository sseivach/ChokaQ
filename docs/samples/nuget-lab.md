# Local NuGet Lab

`samples/ChokaQ.Sample.NuGetLab` is the package-consumer smoke application for
ChokaQ. It exists for one specific reason: it proves that a normal host
application can install the top-level `ChokaQ` package from a NuGet feed and use
the product without source `ProjectReference` shortcuts.

Open `samples/ChokaQ.Sample.NuGetLab/ChokaQ.Sample.NuGetLab.sln` in an IDE. The
solution is deliberately separate from the root `ChokaQ.sln`: the root solution
validates source development, while NuGetLab validates the packaged consumer
experience.

The lab is not a benchmark and it is not a production template. It is a
high-signal validation app that exercises the features a new user is most likely
to care about before trusting a background job engine.

## What This Sample Proves

The lab validates the future NuGet consumer experience:

- the app references only `ChokaQ` as its top-level package;
- the `ChokaQ` package brings in `ChokaQ.Abstractions`, `ChokaQ.Core`,
  `ChokaQ.Storage.SqlServer`, and `ChokaQ.TheDeck` transitively;
- the app starts against SQL Server storage;
- ChokaQ can provision its schema on startup;
- jobs can be enqueued through `IChokaQQueue`;
- typed Bus jobs are dispatched through `ChokaQJobProfile`;
- handlers run through dependency injection and middleware;
- The Deck can connect to the running app;
- health checks report SQL, worker, and queue-saturation status;
- idempotency keys collapse duplicate active work;
- delayed jobs become eligible later;
- retry, throttling, timeout, and fatal-DLQ paths are visible;
- queue pause/resume and worker scaling work through public APIs.

If this app fails to restore or run from local packages, the package shape is not
ready to publish.

## Why It Uses SQL Server

The preview's durable path is SQL Server mode. In-memory mode is useful for
demos, tests, and volatile local experiments, but it is process-local and does
not survive restart. A NuGet smoke app that only tests in-memory execution would
miss the most important parts of ChokaQ:

- persisted `JobsHot` admission;
- SQL schema creation;
- worker ownership;
- atomic Hot-to-Archive and Hot-to-DLQ transitions;
- queue pause/resume in the database;
- dashboard history and DLQ views;
- health checks that depend on storage.

For that reason, NuGetLab intentionally starts with SQL.

## Run The Lab

From the repository root, build local packages into `artifacts/packages`.

```powershell
New-Item -ItemType Directory -Force artifacts\packages | Out-Null
dotnet build ChokaQ.sln --configuration Release --no-restore
dotnet pack src\ChokaQ.Abstractions\ChokaQ.Abstractions.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Core\ChokaQ.Core.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ\ChokaQ.csproj --configuration Release --no-build --no-restore -o artifacts\packages
```

Start SQL Server. The repository compose file starts a SQL container named
`chokaq-sql`.

```powershell
docker compose up -d sqlserver
```

Run the lab.

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --no-cache --force-evaluate
dotnet build samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --configuration Release --no-restore
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj
```

The launch profile starts the app in Development mode. In Development mode the
lab can discover a running Docker container named `chokaq-sql`, read its
published SQL port and `MSSQL_SA_PASSWORD`, and build a local connection string
automatically.

If you want to use another SQL Server instance, provide an explicit connection
string:

```powershell
$env:CHOKAQ_NUGET_LAB_SQL="Server=localhost,1433;Database=ChokaQNuGetLab;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;"
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --no-cache --force-evaluate
dotnet build samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --configuration Release --no-restore
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj
```

If the configured database does not exist, the sample creates it through
`master`, then lets ChokaQ create or update its own schema. The SQL login needs
database creation permission for the first run.

## What To Open

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

This enqueues a blend of work across `critical`, `reports`, and `unstable`
queues:

- receipt email jobs;
- report rendering jobs;
- webhook delivery jobs;
- payment capture jobs;
- intentionally slow jobs;
- throttled partner jobs;
- some delayed jobs.

Use this scenario to confirm that jobs enter SQL, workers drain them, The Deck
updates, and queue-level signals make sense.

### Idempotency Probe

Endpoint:

```text
POST /api/lab/scenarios/idempotency
```

This sends five payment jobs with the same business idempotency key. The
expected behavior is one active logical operation in `JobsHot`. This does not
mean ChokaQ makes payment processors exactly-once. It means ChokaQ can suppress
duplicate active queue admissions for a deterministic operation key.

Handlers that charge cards, send emails, or call external APIs still need
provider-side idempotency, unique constraints, or application-level dedupe.

### Failure Matrix

Endpoint:

```text
POST /api/lab/scenarios/failures
```

This creates four different failure families:

| Job | Expected behavior |
|---|---|
| Poison payload | Moves to DLQ as fatal work. |
| Transient webhook | Retries and then either succeeds or exhausts retry policy. |
| Throttled partner | Uses retry-after semantics before retrying. |
| Slow job | Hits the configured timeout and becomes visible as timeout work. |

Use this scenario to inspect failure taxonomy in The Deck. Do not treat every
DLQ row the same way. A `Fatal` payload usually needs code or data repair; a
`Throttled` row often means the downstream dependency needs time or reduced
concurrency.

### Delayed Jobs

Endpoint:

```text
POST /api/lab/scenarios/delayed
```

This schedules jobs into the future:

| Job | Queue | Delay |
|---|---|---|
| Receipt email | `critical` | About 30 seconds |
| Report render | `reports` | About 45 seconds |

The important behavior is that these jobs are accepted and stored immediately,
but workers must not run them immediately. ChokaQ writes them into SQL
`JobsHot` with a future `ScheduledAtUtc`. Until that time arrives, the SQL fetch
query treats the rows as not eligible.

Think of it as "stored now, runnable later." It is not a process-local
`Task.Delay`, and it is not a timer that disappears when the app restarts. The
schedule is persisted with the job row. If the NuGetLab host restarts while the
job is waiting, the job is still in SQL and can run after it becomes due.

What to observe:

1. Click `Delayed enqueue`.
2. The API response says two jobs were enqueued.
3. They should not immediately become `Processing`.
4. After roughly 30 and 45 seconds, they become eligible.
5. Workers then process them through the normal lifecycle:
   `Pending -> Fetched -> Processing -> Succeeded`.

In the current preview, The Deck does not yet have a dedicated `DELAYED` badge
or `Scheduled For` column. That UI improvement is tracked separately. For now,
the important validation is that future-due jobs are persisted, do not run early,
and later enter the normal worker lifecycle.

## Runtime Controls

NuGetLab includes operational controls that call the same public APIs a real
host would use:

| Control | API | What it validates |
|---|---|---|
| Set workers to 1/4/8 | `POST /api/lab/workers/{count}` | Runtime worker capacity changes. |
| Pause `unstable` | `POST /api/lab/queues/unstable/pause` | Workers skip paused queues. |
| Resume `unstable` | `POST /api/lab/queues/unstable/resume` | Paused work can drain again. |
| Limit `reports` | `POST /api/lab/queues/reports/max-workers/1` | Queue-level concurrency isolation. |
| Unlimit `reports` | `POST /api/lab/queues/reports/max-workers/0` | Removes the per-queue cap. |

These controls are intentionally simple. The Deck provides richer UI workflows,
but the lab makes the HTTP calls obvious and repeatable.

## Troubleshooting

### The App Cannot Find A Connection String

Use one of these options:

- start the repository SQL container with `docker compose up -d sqlserver`;
- set `CHOKAQ_NUGET_LAB_SQL`;
- set `ConnectionStrings:ChokaQDb`;
- set `ChokaQ:SqlServer:ConnectionString`.

Development-mode Docker discovery only looks for a container named
`chokaq-sql`.

### Restore Cannot Find ChokaQ

Build the local packages first. The lab's `NuGet.config` points at
`artifacts/packages`. If that folder does not contain `ChokaQ.0.1.0-preview.1`
and the related internal packages, restore will fail.

If you repack the same preview version during local development, clear the
cached `ChokaQ*` packages or restore with a fresh package cache. NuGet package
caches are version-based, so a newer local `.nupkg` with the same version can be
ignored until the cached copy is removed.

### The First Startup Logs Missing Table Errors

On a brand-new database, hosted workers can start while schema initialization is
running. The important signal is the final startup state:

- `ChokaQ database initialization completed successfully`;
- `/health` returns `Healthy`;
- `/api/lab/snapshot` returns JSON.

If missing-table errors continue after initialization, the schema did not
complete or the app is connecting to a different database/schema than expected.

### `/health` Is Unhealthy

Open `/health`, check the application logs, and inspect these likely causes:

- SQL Server container is still starting;
- SQL password or port is wrong;
- the app cannot create the database;
- schema initialization failed;
- worker heartbeat is stale because the process is overloaded;
- queue lag crossed the configured unhealthy threshold.

Use [SLOs And Alerts](/5-operations/slo-alerts) and
[Operations Runbooks](/5-operations/runbooks) for response guidance.
