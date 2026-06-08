# ChokaQ NuGet Lab Sample

This sample validates the package consumer path: it references the public
`ChokaQ` package through NuGet, not through source `ProjectReference` entries.

Open `ChokaQ.Sample.NuGetLab.sln` when using Visual Studio or Rider. The
solution contains only this host app on purpose, so package restore behaves like
it would in a consumer repository.

## What It Exercises

- top-level `ChokaQ` package restore from nuget.org;
- SQL Server storage and schema auto-provisioning;
- The Deck at `/chokaq`;
- ASP.NET Core health checks at `/health`;
- Bus profiles and typed handlers;
- custom middleware;
- result idempotency for duplicate payment jobs;
- delayed enqueue;
- transient retry, throttled retry-after, fatal DLQ, and timeout paths;
- queue pause/resume and runtime worker scaling through public APIs.

## Run From Published Packages

From the repository root:

```powershell
docker compose up -d sqlserver
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

The launch profile sets `ASPNETCORE_ENVIRONMENT` to `Development`. In that mode
the sample can discover a running Docker container named `chokaq-sql` and build
the connection string from its published SQL port and `MSSQL_SA_PASSWORD`
environment variable.

Open:

| URL | Purpose |
|---|---|
| `http://localhost:5317/` | Lab launcher with scenario buttons and live snapshot. |
| `http://localhost:5317/chokaq` | The Deck dashboard. |
| `http://localhost:5317/health` | ASP.NET Core health endpoint. |

## Use Another SQL Server

If you want to use another SQL Server instance, provide an explicit connection
string:

```powershell
$env:CHOKAQ_NUGET_LAB_SQL="Server=localhost,1433;Database=ChokaQNuGetLab;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;"
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source https://api.nuget.org/v3/index.json
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

If the configured database does not exist, the sample creates it through
`master` before ChokaQ creates its schema. The SQL login therefore needs database
creation permission for first run.

## Validate Local Packages

For release engineering, you can still restore from locally packed packages.
Build packages first:

```powershell
New-Item -ItemType Directory -Force artifacts\packages | Out-Null
dotnet build ChokaQ.sln --configuration Release --no-restore
dotnet pack src\ChokaQ.Abstractions\ChokaQ.Abstractions.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Core\ChokaQ.Core.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj --configuration Release --no-build --no-restore -o artifacts\packages
dotnet pack src\ChokaQ\ChokaQ.csproj --configuration Release --no-build --no-restore -o artifacts\packages
```

Then restore the lab with an explicit local source:

```powershell
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --source artifacts\packages --source https://api.nuget.org/v3/index.json --no-cache --force-evaluate
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj --no-restore
```

If you rebuild local packages with the same preview version, clear the cached
`ChokaQ*` packages or restore with a fresh package cache before running this
sample. NuGet package caches are version-based, so a newer local `.nupkg` with
the same version can be ignored until the cached copy is removed.
