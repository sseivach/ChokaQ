# Schema Bootstrap And Migrations

![Schema bootstrap and migrations](/diagrams/50-schema-bootstrap-migrations.png)

ChokaQ может инициализировать SQL schema при startup, когда включен `AutoCreateSqlTable`. Это полезно для local development, samples и controlled environments, где application identity имеет schema permissions.

## Startup flow

1. `DbMigrationWorker` стартует вместе с host.
2. Он resolves `SqlInitializer`.
3. `SqlInitializer` validates configured schema name.
4. Открывает SQL Server с configured command timeout.
5. Загружает embedded SQL templates.
6. Заменяет `{SCHEMA}` placeholders.
7. Делит script batches по `GO`.
8. Выполняет schema и cleanup procedure scripts.
9. `SchemaMigrations` records applied ChokaQ schema versions.

## Schema name safety

Schema name должен соответствовать alphanumeric и underscore characters. Это не дает schema configuration стать SQL injection input.

## Production guidance

Для production решите явно:

| Mode | Use when |
|---|---|
| Auto bootstrap | Controlled app identity имеет schema permissions, и startup migration acceptable. |
| External migration | DBAs или deployment pipelines владеют schema changes. |

Если production не разрешает `CREATE SCHEMA` / `CREATE TABLE`, запустите embedded schema через deployment process и отключите auto-create permissions для app identity.

## First-start errors

Clean first start должен показывать schema initialization start/completion без `Invalid object name`, missing table errors или unhandled SQL exceptions.

NuGetLab clean database run - правильный validation path для package consumer readiness.

## Архитектурное решение

### Почему этот pattern?

Bootstrap упрощает local и sample usage, а `SchemaMigrations` дает operators auditable ledger applied ChokaQ schema versions.

### Trade-offs

Application-owned schema creation удобен, но может быть неприемлем в locked-down production environments. Поэтому те же schema scripts должны быть usable в deployment pipeline.

### Рассмотренные альтернативы

| Alternative | Benefit | Cost |
|---|---|---|
| Manual SQL only | Strong DBA control. | Poor local onboarding. |
| EF migrations | Familiar для многих .NET teams. | Adds dependency и не соответствует hand-tuned SQL templates. |
| No migration ledger | Проще. | Сложнее support и upgrade diagnosis. |

### Дополнительные вопросы

**Почему validate schema name?**  
Потому что schema injected into SQL templates и не может parameterize'иться как value.

**Зачем migrations ledger?**  
Чтобы знать, какая ChokaQ schema version реально applied в database.

**Production apps должны создавать tables on startup?**  
Только если это explicit operational choice. Иначе migrations должны выполняться before deployment.
