# Docker Compose Sample

Самый быстрый способ проверить ChokaQ с SQL Server и The Deck - root Docker Compose file. Он запускает:

- SQL Server 2022 Developer Edition;
- Bus sample app;
- ChokaQ SQL storage с включенным auto-provisioning;
- The Deck dashboard на `/chokaq`;
- ASP.NET Core health checks на `/health`.

## Run

Из repository root:

```powershell
docker compose up --build
```

Откройте:

```text
http://localhost:5299
http://localhost:5299/chokaq
http://localhost:5299/health
```

Launcher page может enqueue sample jobs в queues `emails`, `background` и `critical`. The Deck позволяет inspect Hot table, Archive, DLQ, queue-lag health, failure taxonomy, bulk operations и queue controls.

## SQL Server

SQL container exposed на host port `14333`, чтобы не конфликтовать с локальным SQL Server instance на `1433`.

```text
Server=localhost,14333;Database=ChokaQSample;User Id=sa;Password=<password>;Encrypt=True;TrustServerCertificate=True;
```

Compose file по умолчанию использует явно помеченный development password:

```text
ChokaQ_dev_only_ChangeMe_2026!
```

Override при необходимости:

```powershell
$env:CHOKAQ_SQL_SA_PASSWORD = "Use_a_local_dev_password_123!"
docker compose up --build
```

Этот password предназначен только для local sample infrastructure. Production applications должны использовать normal secret management и не хранить SQL credentials в source-controlled appsettings files.

## Reset

Остановить sample, сохранив SQL data:

```powershell
docker compose down
```

Удалить SQL volume и стартовать с empty database:

```powershell
docker compose down -v
```

## Why this sample exists

Compose sample - smoke test для реального operator path:

1. app читает runtime policy из configuration и environment variables;
2. SQL storage idempotently создает schema;
3. workers fetch from SQL instead of in-memory channels;
4. The Deck читает тот же storage, что и workers;
5. health checks expose readiness через standard ASP.NET Core endpoints.

Это делает sample полезным и для first-time users, и для maintainers, которым нужно проверить end-to-end SQL experience после изменений.
