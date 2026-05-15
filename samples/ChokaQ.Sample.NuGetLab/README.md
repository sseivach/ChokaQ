# ChokaQ NuGet Lab Sample

This sample validates the package consumer path: it references `ChokaQ` through
NuGet, not through source `ProjectReference` entries.

Open `ChokaQ.Sample.NuGetLab.sln` when using Visual Studio or Rider. The
solution contains only this host app on purpose, so package restore behaves like
it would in a consumer repository.

## What It Exercises

- top-level `ChokaQ` package restore from a local feed;
- SQL Server storage and schema auto-provisioning;
- The Deck at `/chokaq`;
- ASP.NET Core health checks at `/health`;
- Bus profiles and typed handlers;
- custom middleware;
- result idempotency for duplicate payment jobs;
- delayed enqueue;
- transient retry, throttled retry-after, fatal DLQ, and timeout paths;
- queue pause/resume and runtime worker scaling through public APIs.

## Run

From the repository root:

```powershell
New-Item -ItemType Directory -Force artifacts\packages | Out-Null
dotnet pack src\ChokaQ.Abstractions\ChokaQ.Abstractions.csproj --configuration Release --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Core\ChokaQ.Core.csproj --configuration Release --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.Storage.SqlServer\ChokaQ.Storage.SqlServer.csproj --configuration Release --no-restore -o artifacts\packages
dotnet pack src\ChokaQ.TheDeck\ChokaQ.TheDeck.csproj --configuration Release --no-restore -o artifacts\packages
dotnet pack src\ChokaQ\ChokaQ.csproj --configuration Release --no-restore -o artifacts\packages
```

Then point the sample at a SQL Server instance:

```powershell
$env:CHOKAQ_NUGET_LAB_SQL="Server=localhost,1433;Database=ChokaQNuGetLab;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;"
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --no-cache --force-evaluate
dotnet build samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --configuration Release --no-restore
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj
```

For local development, the launch profile sets `ASPNETCORE_ENVIRONMENT` to
`Development`. In that mode the sample can discover a running Docker container
named `chokaq-sql` and build the connection string from its published SQL port
and `MSSQL_SA_PASSWORD` environment variable:

```powershell
docker compose up -d sqlserver
dotnet restore samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --no-cache --force-evaluate
dotnet build samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.sln --configuration Release --no-restore
dotnet run --project samples\ChokaQ.Sample.NuGetLab\ChokaQ.Sample.NuGetLab.csproj
```

If you rebuild local packages with the same preview version, clear the cached
`ChokaQ*` packages or restore with a fresh package cache before running this
sample. Otherwise the app can keep using an older local package even though
`artifacts/packages` has newer `.nupkg` files.

If the configured database does not exist, the sample creates it through
`master` before ChokaQ creates its schema. The SQL login therefore needs database
creation permission for first run.

Open the printed local URL for the lab UI, `/health` for host health, and
`/chokaq` for The Deck.
