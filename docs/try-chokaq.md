# Try ChokaQ

This page is the shortest path from "what is this?" to a running SQL-backed
ChokaQ host with The Deck open in a browser.

The goal is not to configure a production system. The goal is to verify the
preview install path, run real jobs, and see how ChokaQ exposes queue state,
failures, delayed work, health, and operator controls.

## Prerequisites

- .NET 10 SDK.
- Docker Desktop, or another reachable SQL Server instance.
- A browser.

## Install The Package

The public preview install target is the top-level `ChokaQ` package:

```powershell
dotnet add package ChokaQ --version 0.1.0-preview.1
```

That package brings in the runtime subpackages transitively:

- `ChokaQ.Abstractions`;
- `ChokaQ.Core`;
- `ChokaQ.Storage.SqlServer`;
- `ChokaQ.TheDeck`.

## Run The SQL Sample

Clone the repository and start the SQL Server container:

```powershell
git clone https://github.com/sseivach/ChokaQ
cd ChokaQ
docker compose up -d sqlserver
```

Restore the NuGetLab sample from nuget.org and run it:

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

The sample runs in Development mode by default. In that mode it can discover the
repository SQL container named `chokaq-sql`, read the published SQL port and
`MSSQL_SA_PASSWORD`, create a local database, and let ChokaQ provision its schema.

## Open The App

Open these URLs:

| URL | What it shows |
|---|---|
| `http://localhost:5317/` | Lab launcher with scenario buttons and a live snapshot. |
| `http://localhost:5317/chokaq` | The Deck dashboard. |
| `http://localhost:5317/health` | ASP.NET Core health endpoint. |

## Run The Scenarios

From the lab launcher, run these scenarios:

| Scenario | What to look for |
|---|---|
| Mixed load | Jobs appear across queues, workers drain them, Archive count increases. |
| Idempotency probe | Duplicate active work collapses through the same business key. |
| Delayed jobs | Work is stored now and becomes eligible later. |
| Failure matrix | Failed work moves to DLQ and top error signals become visible. |

In The Deck, check:

- queue rows and lag values;
- summary counters;
- active/archive/DLQ views;
- top error types;
- system health and circuit breaker state;
- queue pause/resume and worker limit controls.

## Expected Result

A healthy first run should show:

- `/health` returns a successful response;
- `/chokaq` loads The Deck assets;
- successful jobs move out of active work and into Archive;
- failed jobs are visible in DLQ;
- delayed jobs do not run before their due time;
- the lab snapshot and The Deck agree on broad queue state.

## If SQL Discovery Fails

The sample looks for a Docker container named `chokaq-sql` when it runs in
Development mode. Start the repository SQL service first:

```powershell
docker compose up -d sqlserver
```

If you want to use your own SQL Server, set an explicit connection string:

```powershell
$env:CHOKAQ_NUGET_LAB_SQL="Server=localhost,1433;Database=ChokaQNuGetLab;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;"
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj
```

The SQL login needs permission to create the database on the first run. After
the database exists, ChokaQ creates or updates its own schema.

## If Restore Uses The Wrong Package Source

For preview testing, restore from nuget.org explicitly:

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
```

This avoids accidentally restoring from a local package source if you previously
used explicit local-package validation commands inside the repository.

## Give Feedback

If setup breaks, the docs are unclear, or The Deck behaves strangely, open a
preview feedback issue:

```text
https://github.com/sseivach/ChokaQ/issues/new?template=preview-feedback.yml
```

Useful feedback includes:

- operating system;
- .NET SDK version;
- whether SQL Server was Docker or your own instance;
- exact command that failed;
- exception text or screenshot;
- whether the problem was install, docs, runtime behavior, or UI.

## Next Reading

- [Getting Started](/getting-started)
- [Local NuGet Lab](/samples/nuget-lab)
- [Delivery Guarantees](/delivery-guarantees)
- [The Deck Panel Guide](/4-the-deck/panel-guide)
