# Docker Compose Sample

The fastest way to exercise ChokaQ with SQL Server and The Deck is the root
Docker Compose file. It starts:

- SQL Server 2022 Developer Edition;
- the Bus sample app;
- ChokaQ SQL storage with auto-provisioning enabled;
- The Deck dashboard at `/chokaq`;
- ASP.NET Core health checks at `/health`.

## Run

From the repository root:

```powershell
docker compose up --build
```

Open:

```text
http://localhost:5299
http://localhost:5299/chokaq
http://localhost:5299/health
```

The launcher page can enqueue sample jobs into the `emails`, `background`, and
`critical` queues. The Deck lets you inspect the Hot table, Archive, DLQ,
queue-lag health, failure taxonomy, bulk operations, and queue controls.

## SQL Server

The SQL container is exposed on host port `14333` to avoid clashing with a local
SQL Server instance on `1433`.

```text
Server=localhost,14333;Database=ChokaQSample;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;
```

The compose file uses a clearly marked development password by default:

```text
ChokaQ_dev_only_ChangeMe_2026!
```

Override it when needed:

```powershell
$env:CHOKAQ_SQL_SA_PASSWORD = "Use_a_local_dev_password_123!"
docker compose up --build
```

This password is for local sample infrastructure only. Production applications
should use normal secret management and should not keep SQL credentials in
source-controlled appsettings files.

## Reset

To stop the sample while preserving SQL data:

```powershell
docker compose down
```

To remove the SQL volume and start from an empty database:

```powershell
docker compose down -v
```

## Why This Sample Exists

The compose sample is a smoke test for the real operator path:

1. the app reads runtime policy from configuration and environment variables;
2. SQL storage creates the schema idempotently;
3. workers fetch from SQL instead of in-memory channels;
4. The Deck reads the same storage as the workers;
5. health checks expose readiness through standard ASP.NET Core endpoints.

That makes the sample useful both for first-time users and for maintainers who
want to verify that the end-to-end SQL experience still works after a change.
