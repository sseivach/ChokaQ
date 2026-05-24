# Production Readiness Checklist

![Production readiness map](/diagrams/08-production-readiness-map.png)

Используйте этот checklist перед включением ChokaQ для production work.

## Package And Runtime

- Устанавливайте top-level package `ChokaQ`, если вы не тестируете internal project references намеренно.
- Pin preview version явно.
- Проверьте, что app стартует с тем же package graph, который будет deployed.
- Перед release прогоните NuGetLab sample на fresh database.

## SQL Server

- Используйте dedicated database или schema.
- Убедитесь, что app identity имеет нужные bootstrap permissions, если `AutoCreateSqlTable = true`.
- Если bootstrap permissions запрещены в production, запускайте schema creation через deployment migration step.
- Установите `CommandTimeoutSeconds` так, чтобы workers и dashboard operations fail fast достаточно быстро.
- Размер connection pools должен учитывать workers, dashboard reads, health checks и admin operations.
- Monitor database CPU, waits, lock waits, deadlocks и log growth.

## Storage Retention

- Определите Archive retention.
- Определите DLQ retention.
- Решите, требует ли purge approval.
- Оставляйте достаточно DLQ history для incident investigation.
- Не позволяйте audit retention стать accidental admission control.

## Worker Policy

- Настройте retry limits исходя из downstream behavior.
- Добавьте jitter, чтобы избежать synchronized retry storms.
- Настройте `ProcessingZombieTimeout` выше normal handler duration и heartbeat jitter.
- Настройте queue-specific timeouts для long-running queues.
- Используйте max worker caps для downstream-limited queues.

## Handler Safety

- Считайте execution at-least-once.
- Используйте business idempotency keys для external side effects.
- Используйте provider idempotency для payments и webhooks.
- Используйте unique constraints или upserts для database writes.
- Избегайте non-versioned payload contracts.
- Не полагайтесь на CLR type names как persisted contracts.

## The Deck

- Требуйте authentication.
- По возможности используйте отдельную destructive authorization policy для purge/requeue/cancel.
- Отключайте anonymous access вне demos.
- Проверьте, что `/chokaq` загружает static assets из packaged application.
- Документируйте, кто имеет право управлять DLQ и queue controls.

Подробная security model dashboard описана в [Authorization Model](/ru/4-the-deck/authorization-model) и [Destructive Actions](/ru/4-the-deck/destructive-actions).

## Health Checks

- Expose health checks для infrastructure, не для public internet.
- Включите SQL connectivity.
- Включите worker heartbeat.
- Включите queue lag/saturation.
- Alert'ьте stale worker heartbeat, high lag и growing DLQ.

Подробная semantics probes описана в [Health Checks](/ru/5-operations/health-checks).

## Metrics And Logs

- Export meter `ChokaQ`.
- Ограничьте metric tag cardinality.
- Alert'ьте queue lag, failure rate, DLQ growth, heartbeat failures и open circuits.
- Храните structured logs для worker lifecycle, retry scheduling, DLQ moves, resurrection, purge и circuit events.

## Deployment Smoke Test

Перед объявлением release healthy:

1. Начните с fresh database.
2. Убедитесь, что schema bootstrap или migration завершается без first-start SQL errors.
3. Enqueue successful job.
4. Убедитесь, что он появляется в `JobsHot`.
5. Убедитесь, что он переходит в `JobsArchive`.
6. Enqueue failing job.
7. Проверьте retry behavior.
8. Проверьте DLQ move.
9. Откройте The Deck.
10. Убедитесь, что `/health` reports healthy или expected degraded status.

## Архитектурное решение

### Почему этот checklist?

Большинство production failures происходит не из-за одного missing API call. Они появляются на границе code, database, deployment, observability и operator permissions. Этот checklist принудительно включает эти границы в release process.

### Дополнительные вопросы

**За какой production metric смотреть первым?**  
Queue lag. Depth важен, но lag говорит, как долго eligible work ждет.

**Самое рискованное operator action?**  
Bulk purge, потому что он уничтожает recovery evidence.

**Самый рискованный handler bug?**  
Non-idempotent external side effect вместе с retry или zombie resurrection.
