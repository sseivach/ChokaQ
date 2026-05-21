# Schema Bootstrap And Migrations

![Schema bootstrap and migrations](/diagrams/50-schema-bootstrap-migrations.png)

ChokaQ can initialize its SQL schema at startup when `AutoCreateSqlTable` is
enabled. This is useful for local development, samples, and controlled
environments where the application identity has schema permissions.

## Startup Flow

1. `DbMigrationWorker` starts with the host.
2. It resolves `SqlInitializer`.
3. `SqlInitializer` validates the configured schema name.
4. It opens SQL Server with the configured command timeout.
5. It loads embedded SQL templates.
6. It replaces `{SCHEMA}` placeholders.
7. It splits script batches on `GO`.
8. It executes schema and cleanup procedure scripts.
9. `SchemaMigrations` records applied ChokaQ schema versions.

## Schema Name Safety

The schema name must match alphanumeric and underscore characters. This prevents
schema configuration from becoming SQL injection input.

## Production Guidance

For production, decide explicitly:

| Mode | Use when |
|---|---|
| Auto bootstrap | Controlled app identity has schema permissions and startup migration is acceptable. |
| External migration | DBAs or deployment pipelines own schema changes. |

If production does not allow `CREATE SCHEMA` / `CREATE TABLE`, run the embedded
schema through your deployment process and disable auto-create permissions for
the app identity.

## First-Start Errors

A clean first start should show schema initialization start/completion without
`Invalid object name`, missing table errors, or unhandled SQL exceptions.

The NuGetLab clean database run is the right validation path for package
consumer readiness.

## Architecture Decision

### Why this pattern?

Bootstrap makes local and sample usage simple while `SchemaMigrations` gives
operators an auditable ledger of applied ChokaQ schema versions.

### Trade-offs

Application-owned schema creation is convenient but may be unacceptable in
locked-down production environments. That is why the same schema scripts must
also be usable in a deployment pipeline.

### Alternatives considered

| Alternative | Benefit | Cost |
|---|---|---|
| Manual SQL only | Strong DBA control. | Poor local onboarding. |
| EF migrations | Familiar to many .NET teams. | Adds dependency and does not match hand-tuned SQL templates. |
| No migration ledger | Simpler. | Harder support and upgrade diagnosis. |

### Additional Questions

**Why validate schema name?**  
Because schema is injected into SQL templates and cannot be parameterized like a
value.

**Why keep a migrations ledger?**  
To know what ChokaQ schema version a database has actually applied.

**Should production apps create tables on startup?**  
Only when that is an explicit operational choice. Otherwise migrations should
run before deployment.

